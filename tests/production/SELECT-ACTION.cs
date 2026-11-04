/*
SELECT-ACTION@1 test-first contract, plain C# 9, resource isolated-csharp.
Sources: plan SELECT-ACTION; m3/test-engineer.json suite_checks + A3/A5/A8;
SDoc BP-UI-02 UI02-SELECT/SNAP/MENU/COMMAND and BP-PRES-02 PRES02-CURSORS.
Only this suite and red.log change. The runner discovers this package module;
no product, consumers, fixtures, config or exports need editing for the red step.
Real fixtures: seeded WorldState, WorldNavigation, BlockSites slot activities,
CommandArbitrator, ActivityExecutor, ResidentPanel, Accessibility and InputMap.
No mocks, external I/O, editor, display, ports, credentials or wall-clock waits.

Public model seam (reflection follows HUD so absent capabilities print FAIL):
 Blockparty.UI.Selection(WorldState, WorldNavigation, Accessibility)
   PointerSelect(ResidentId? hit, float x, float y, IReadOnlyList<SelectionTarget>)
   FocusResident(ResidentId); bool TryInput(string binding, IReadOnlyList<SelectionTarget>)
   bool HasOutline(ResidentId); string Label, Message
 SelectionTarget(ResidentId, float x, float y): visible/selectable projected
   centers, in screen pixels, supplied in declared keyboard reading order.
 PointerSelect uses the hit when snap is off; with snap on it uses nearest
 projected center (tie: lowest ResidentId). Empty candidates clear selection
 with a no-match message. Focus and SelectNext/Previous use WorldNavigation.
 Blockparty.UI.ActionMenu(WorldState, CommandArbitrator, Accessibility, InputMap)
   void Open(ResidentId, ObjectId, string label, IReadOnlyList<AvailableAction>)
   IReadOnlyList<AvailableAction> Activities; string Label, Message, FocusedElementId
   CommandResult Choose(ActivityId, QueueMode)
   IReadOnlyList<HudElement> Elements(int width, int height)
   bool TryInput(string binding), TryClick(string elementId)
 Open consumes object-bound catalog offers from the caller (here BlockSites),
 never all global activities. Elements reuse HUD's model and expose one choice
 per offered activity/mode, ids action:<activity-code>:Immediate|Append.
 SelectNext/Previous move focus, Confirm activates, and pointer uses TryClick.
 Blockparty.UI.CursorState(Accessibility)
   object Resolve(bool interactable, bool resident, RejectReason? blocked,
                  bool busy, bool placement)
 Cue readable members: Kind (ToString yields Default, HoverInteractable,
 HoverResident, Blocked, Busy, Placement), ShapeId, Label. Blocked includes its
 reason. Priority: busy > blocked > placement > resident > interactable > default.
 These are instance presentation models; new instances start unselected.

Local mapping, NOT full SEIT/build acceptance:
 SelectionAndFocus, SnapGridAndTie, KeyboardAndTiming -> A3/A8, SEIT-UI-02-P1.
 ObjectMenu, CommandModes -> SEIT-IF-CMD-P1/P2, SEIT-UI-02-P1.
 AppendPolicyAndBound -> SEIT-SIM-04-P2/N2; CursorTable -> six-state table.
 MenuAccessibility -> A3/A5 (local menu; HUD root traversal stays in HUD).
 ResidentPanel has no issue-activity API: direct Submit is the normal-envelope
 oracle; its result is inspected via the real ResidentPanel path.
 Negative fixture changes an OBSERVED full-queue refusal to Accepted, then
 runs the identical bound oracle; it must fail. Real producers are checked
 before missing models. No unconditional negative-fixture failure is used.
 Projected test centers are explicit 1080p view inputs; world metre distances
 deliberately disagree. Rendering, real input timing, full HUD/menu focus
 integration, audible/visual review remain CONTROLS-PLAY on the i7.
 No durable selection/menu state is introduced; settings persistence is already
 INPUT-ACCESS's public-save check. No schema-version oracle belongs here.
*/
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Blockparty.Contracts;
using Blockparty.Simulation;
using Blockparty.UI;

public static class SelectActionChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private static int failures;
    private static bool negativeFixture;

    private static void Check(bool ok, string reason)
    {
        if (!ok) throw new InvalidOperationException(reason);
    }
    private static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            failures++;
            Console.WriteLine("FAIL " + name + ": " + e.Message);
        }
    }
    private static Type Product(string name)
    {
        Type type = typeof(WorldNavigation).Assembly.GetType("Blockparty.UI." + name);
        Check(type != null, "missing SELECT-ACTION capability Blockparty.UI." + name);
        return type;
    }
    private static object New(string name, params object[] args)
    {
        return Activator.CreateInstance(Product(name), args);
    }
    private static object Call(object model, string name, params object[] args)
    {
        MethodInfo method = model.GetType().GetMethod(name);
        Check(method != null, "missing SELECT-ACTION capability " + model.GetType().Name + "." + name);
        return method.Invoke(model, args);
    }
    private static T Read<T>(object model, string name)
    {
        Check(model != null, "null SELECT-ACTION projection " + name);
        PropertyInfo prop = model.GetType().GetProperty(name);
        if (prop != null) return (T)prop.GetValue(model);
        FieldInfo field = model.GetType().GetField(name);
        Check(field != null, "missing SELECT-ACTION capability " + model.GetType().Name + "." + name);
        return (T)field.GetValue(model);
    }
    private static string Choice(ActivityId activity, QueueMode mode)
    {
        return "action:" + activity.Code + ":" + mode;
    }

    private sealed class Fixture
    {
        public readonly WorldState World = WorldState.CreateSeeded(7L, 4242L, 0, null);
        public readonly Clock Clock;
        public readonly CommandArbitrator Commands;
        public readonly ActivityExecutor Executor;
        public readonly WorldNavigation Nav;
        public readonly ResidentPanel Panel;
        public readonly Accessibility Access = new Accessibility(new SettingsRecord(1, 1f, 1f, false));
        public readonly InputMap Input;
        public readonly ResidentRecord A, B, C;
        public readonly BlockSiteSlot Office, School;
        public Fixture()
        {
            HomeRecord home = World.AllocateHome(Core, 8);
            HouseholdRecord house = World.AllocateHousehold(Core, "Residents", home.Id);
            A = World.AllocateResident(Core, "Alex", LifeStage.Adult, house.Id, home.Id);
            B = World.AllocateResident(Core, "Blair", LifeStage.Adult, house.Id, home.Id);
            C = World.AllocateResident(Core, "Casey", LifeStage.Adult, house.Id, home.Id);
            Clock = new Clock(World);
            Commands = new CommandArbitrator(World, Clock);
            Executor = new ActivityExecutor(World, Commands, Clock);
            Nav = new WorldNavigation(World, Commands, Executor);
            Panel = new ResidentPanel(World, Commands);
            Input = new InputMap(Access, Clock);
            Executor.SetResidentPosition(A.Id, new ApproachPose(100f, 0f, 0f));
            Executor.SetResidentPosition(B.Id, new ApproachPose(0f, 0f, 0f));
            Executor.SetResidentPosition(C.Id, new ApproachPose(1f, 0f, 0f));
            var sites = new BlockSites(World, Executor);
            foreach (BlockSite site in sites.Sites)
                foreach (BlockSiteSlot slot in site.Slots)
                {
                    if (slot.Id == "work-jobsearch") Office = slot;
                    if (slot.Id == "attend-school-a") School = slot;
                }
            Check(Office != null && School != null, "real BLOCK-SITES slot producers");
        }
        public object Selection() { return New("Selection", World, Nav, Access); }
        public List<AvailableAction> Offers(BlockSiteSlot slot)
        {
            return new List<AvailableAction> {
                new AvailableAction(new ActivityId(slot.ActivityCode), (long)slot.Object) };
        }
        public object Menu(BlockSiteSlot slot = null)
        {
            slot = slot ?? Office;
            object menu = New("ActionMenu", World, Commands, Access, Input);
            Call(menu, "Open", A.Id, slot.Object, slot.Id, Offers(slot).AsReadOnly());
            return menu;
        }
        public CommandResult Direct(string code, QueueMode mode, long? target = null,
            CommandSource source = CommandSource.PlayerHuman)
        {
            if (source == CommandSource.PlayerAutomated) Commands.EnableAutomation();
            return Commands.Submit(new CommandEnvelope(World.AllocateCommand(Core),
                World.Epoch, World.GameSeconds, source, A.ResidentSequence + 1u,
                CommandKind.DirectActivity, A.Id, new ActivityId(code), target,
                new CommandParameters.Direct(mode)));
        }
        public ResidentPanelSnapshot View()
        {
            ResidentPanelSnapshot view;
            string reason;
            Check(Panel.TryInspect(A.Id, out view, out reason), "real ResidentPanel: " + reason);
            return view;
        }
        public void Fill(int player, int autonomous)
        {
            Clock.Pause();
            for (int i = 0; i < player; i++)
                Check(Direct("ACT-EDU-02", QueueMode.Append).Accepted, "fill real player slot " + i);
            for (int i = 0; i < autonomous; i++)
                Check(Commands.EnqueueAutonomous(A.Id, new ActivityId("ACT-SPORT-01"), null),
                    "fill real autonomous slot " + i);
        }
    }

    private static string Queue(Fixture f)
    {
        var s = new StringBuilder();
        ActionEntry current = f.Commands.GetCurrent(f.A.Id);
        if (current != null) Append(s, current, true);
        foreach (ActionEntry e in f.Commands.GetPending(f.A.Id)) Append(s, e, true);
        return s.ToString();
    }
    private static void Append(StringBuilder s, ActionEntry e, bool ids)
    {
        if (ids) s.Append((long)e.Action).Append(':').Append((long)e.Command).Append(':');
        s.Append(e.Kind).Append(':').Append(e.Activity.Code).Append(':')
            .Append(e.TargetId).Append(':').Append(e.Owner).Append(':').Append(e.Source).Append('|');
    }
    private static string Semantics(ResidentPanelSnapshot view)
    {
        var s = new StringBuilder();
        s.Append("current:");
        if (view.Current != null) Append(s, view.Current, false);
        s.Append("pending:");
        foreach (ActionEntry entry in view.Queue) Append(s, entry, false);
        return s.ToString();
    }
    private static string Authority(Fixture f)
    {
        var s = new StringBuilder();
        s.Append(f.World.ComputeStateHash()).Append(':').Append(f.World.RngState)
            .Append(':').Append(f.World.NextCommandId).Append(':').Append(f.World.NextActionId)
            .Append(':').Append(f.World.WriteAudit.Count).Append(':').Append(f.Commands.CommandLog.Count);
        foreach (ResidentRecord r in f.World.ResidentsSnapshot())
        {
            ApproachPose p = f.Executor.GetResidentPosition(r.Id);
            s.Append('|').Append((long)r.Id).Append(':').Append(p.X).Append(':').Append(p.Y).Append(':').Append(p.Z);
        }
        return s.Append(Queue(f)).ToString();
    }
    private static void BoundOracle(CommandResult result, string before, string after)
    {
        Check(!result.Accepted && result.Reason == RejectReason.QueueFull && before == after,
            "SEIT-SIM-04-N2 Append #9 must reject QueueFull and preserve all 8 entries");
    }
    private static void RealProducersAndNegativeOracle()
    {
        var f = new Fixture();
        Check(f.Office.ActivityCode == "ACT-WORK-03" && f.School.ActivityCode == "ACT-EDU-01",
            "real object activity bindings");
        Check(f.Nav.SelectResident(f.A.Id).Accepted && f.Nav.SelectedResident == f.A.Id,
            "real WorldNavigation selection");
        f.Fill(8, 0);
        string before = Queue(f);
        CommandResult refusal = f.Direct(f.Office.ActivityCode, QueueMode.Append, (long)f.Office.Object);
        Check(!refusal.Accepted && refusal.Reason == RejectReason.QueueFull, "real full-queue producer");
        BoundOracle(negativeFixture ? CommandResult.Accept() : refusal, before, Queue(f));
        Check(f.View().Queue.Count == 8, "real ResidentPanel queue oracle");
    }

    private static Array Targets(Fixture f, bool reversed = false)
    {
        Array targets = Array.CreateInstance(Product("SelectionTarget"), 3);
        // Explicit view projection: world-nearest Blair is screen-farthest.
        targets.SetValue(New("SelectionTarget", f.A.Id, 500f, 500f), reversed ? 2 : 0);
        targets.SetValue(New("SelectionTarget", f.B.Id, 900f, 500f), 1);
        targets.SetValue(New("SelectionTarget", f.C.Id, 540f, 500f), reversed ? 0 : 2);
        return targets;
    }
    private static void Selected(Fixture f, object selection, ResidentId id)
    {
        Check(f.Nav.SelectedResident == id && (bool)Call(selection, "HasOutline", id),
            "selection must use WorldNavigation and outline selected identity");
        ResidentRecord r;
        Check(f.World.TryGetResident(id, out r) && Read<string>(selection, "Label") == r.DisplayName,
            "selected name label must match resident");
        foreach (ResidentRecord other in f.World.ResidentsSnapshot())
            if (other.Id != id) Check(!(bool)Call(selection, "HasOutline", other.Id), "only selected resident outlined");
    }
    private static void SelectionAndFocus()
    {
        var f = new Fixture();
        object selection = f.Selection();
        f.Access.SetSnapSelection(false);
        string before = Authority(f);
        Call(selection, "PointerSelect", (ResidentId?)f.A.Id, 900f, 500f, Targets(f));
        Selected(f, selection, f.A.Id);
        Call(selection, "FocusResident", f.C.Id);
        Selected(f, selection, f.C.Id);
        Check(Authority(f) == before, "click/focus changes no position, queue, RNG or authority");
        var fresh = new Fixture();
        object next = fresh.Selection();
        Check(!fresh.Nav.SelectedResident.HasValue && !(bool)Call(next, "HasOutline", fresh.A.Id),
            "fresh play entry resets presentation selection");
    }
    private static void SnapGridAndTie()
    {
        var f = new Fixture();
        object selection = f.Selection();
        f.Access.SetSnapSelection(true);
        string before = Authority(f);
        foreach (bool reversed in new[] { false, true })
        {
            Array targets = Targets(f, reversed);
            for (int dx = -16; dx <= 16; dx += 8)
                for (int dy = -16; dy <= 16; dy += 8)
                {
                    Call(selection, "PointerSelect", (ResidentId?)f.B.Id, 500f + dx, 500f + dy, targets);
                    Selected(f, selection, f.A.Id);
                    Call(selection, "PointerSelect", null, 540f + dx, 500f + dy, targets);
                    Selected(f, selection, f.C.Id);
                }
            Call(selection, "PointerSelect", null, 520f, 500f, targets);
            Selected(f, selection, f.A.Id);
        }
        Array overlap = Array.CreateInstance(Product("SelectionTarget"), 2);
        overlap.SetValue(New("SelectionTarget", f.C.Id, 500f, 500f), 0);
        overlap.SetValue(New("SelectionTarget", f.A.Id, 500f, 500f), 1);
        Call(selection, "PointerSelect", null, 500f, 500f, overlap);
        Selected(f, selection, f.A.Id);
        Call(selection, "PointerSelect", null, 500f, 500f, Array.CreateInstance(Product("SelectionTarget"), 0));
        Check(!f.Nav.SelectedResident.HasValue && !string.IsNullOrWhiteSpace(Read<string>(selection, "Message")),
            "no candidates clears selection with no-match result");
        Check(!(bool)Call(selection, "HasOutline", f.A.Id) && Authority(f) == before,
            "snap/clear changes presentation only");
    }
    private static void KeyboardAndTiming()
    {
        foreach (double delay in new[] { 0.0, 0.25, 30.0 })
        {
            var f = new Fixture();
            object selection = f.Selection();
            Array targets = Targets(f);
            Check(f.Access.TryRemap("SelectNext", "F8") && f.Access.TryRemap("SelectPrevious", "F9"), "real remaps");
            string before = Authority(f);
            foreach (ResidentRecord r in new[] { f.A, f.B, f.C, f.A })
            {
                f.Input.AdvanceGameSeconds(delay);
                Check(!f.Input.RequiresHold("SelectNext") && (bool)Call(selection, "TryInput", "F8", targets),
                    "keyboard selection resolves remap without hold");
                Selected(f, selection, r.Id);
            }
            Check((bool)Call(selection, "TryInput", "F9", targets), "reverse traversal");
            Selected(f, selection, f.C.Id);
            Check(Authority(f) == before, "keyboard delays do not change authority");
        }
    }
    private static void ObjectMenu()
    {
        var f = new Fixture();
        string before = Authority(f);
        object menu = f.Menu();
        var offers = Read<IReadOnlyList<AvailableAction>>(menu, "Activities");
        Check(offers.Count == 1 && offers[0].Activity.Code == f.Office.ActivityCode
            && offers[0].TargetId == (long)f.Office.Object && Read<string>(menu, "Label") == f.Office.Id,
            "object menu lists only its real bound catalog activity and label");
        Call(menu, "Open", f.A.Id, f.School.Object, f.School.Id, f.Offers(f.School).AsReadOnly());
        offers = Read<IReadOnlyList<AvailableAction>>(menu, "Activities");
        Check(offers.Count == 1 && offers[0].Activity.Code == f.School.ActivityCode
            && offers[0].TargetId == (long)f.School.Object && Authority(f) == before,
            "changing object replaces offers; opening submits no command");
        CommandResult blocked = (CommandResult)Call(menu, "Choose", new ActivityId(f.School.ActivityCode), QueueMode.Append);
        Check(!blocked.Accepted && blocked.Reason == RejectReason.StageIneligible
            && Read<string>(menu, "Message").Contains(blocked.Reason.ToString()), "menu displays existing eligibility refusal");
    }
    private static void CommandModes()
    {
        foreach (QueueMode mode in new[] { QueueMode.Immediate, QueueMode.Append })
        {
            var menuFixture = new Fixture();
            var oracle = new Fixture();
            foreach (Fixture f in new[] { menuFixture, oracle })
            {
                Check(f.Direct("ACT-EDU-02", QueueMode.Immediate).Accepted, "real current activity");
                Check(f.Direct("ACT-SPORT-01", QueueMode.Append).Accepted, "real pending player activity");
            }
            object menu = menuFixture.Menu();
            int logs = menuFixture.Commands.CommandLog.Count;
            Check(((CommandResult)Call(menu, "Choose", new ActivityId(menuFixture.Office.ActivityCode), mode)).Accepted,
                "menu submits chosen normal command " + mode);
            Check(oracle.Direct(oracle.Office.ActivityCode, mode, (long)oracle.Office.Object).Accepted, "normal-envelope oracle");
            Check(menuFixture.Commands.CommandLog.Count == logs + 1
                && menuFixture.Commands.CommandLog[logs].Accepted
                && menuFixture.Commands.CommandLog[logs].Kind == CommandKind.DirectActivity
                && menuFixture.Commands.CommandLog[logs].Resident == menuFixture.A.Id,
                "exactly one arbitrated command");
            Check(Semantics(menuFixture.View()) == Semantics(oracle.View()), "ResidentPanel and normal command queue agree " + mode);
            if (mode == QueueMode.Immediate)
                Check(menuFixture.Commands.Interrupted.Count == 1 && menuFixture.View().Current != null
                    && menuFixture.View().Current.Activity.Code == menuFixture.Office.ActivityCode
                    && menuFixture.View().Queue.Count == 0, "Immediate interrupts once and replaces work");
            else Check(menuFixture.View().Queue.Count == 2
                && menuFixture.View().Queue[0].Activity.Code == "ACT-SPORT-01"
                && menuFixture.View().Queue[1].Activity.Code == menuFixture.Office.ActivityCode,
                "Append preserves player order");
        }
    }
    private static void AppendPolicyAndBound()
    {
        foreach (int players in new[] { 2, 6, 8 })
        {
            var f = new Fixture();
            f.Fill(players, players == 8 ? 0 : 2);
            object menu = f.Menu();
            ActionEntry[] original = f.Commands.GetPending(f.A.Id);
            string before = Queue(f);
            uint generation = f.A.DecisionSlotGeneration;
            CommandResult result = (CommandResult)Call(menu, "Choose", new ActivityId(f.Office.ActivityCode), QueueMode.Append);
            if (players == 8)
            {
                BoundOracle(result, before, Queue(f));
                Check(Read<string>(menu, "Message").Contains("QueueFull"), "QueueFull visible on menu");
                result = (CommandResult)Call(menu, "Choose", new ActivityId(f.Office.ActivityCode), QueueMode.Immediate);
                Check(result.Accepted && f.View().Queue.Count == 1, "paused full Immediate needs no ninth slot");
                f.Clock.Resume();
                f.Clock.AdvanceRealSeconds(Core, 0.1, () => f.Executor.Tick(StateWriter.ActivityExecutor));
                Check(f.Commands.GetCurrent(f.A.Id) != null, "Immediate starts next unpaused tick");
            }
            else
            {
                var queue = f.Commands.GetPending(f.A.Id);
                Check(result.Accepted && queue.Length == players + 1 && f.A.DecisionSlotGeneration > generation,
                    "Append removes non-player work before capacity check");
                for (int i = 0; i < players; i++)
                    Check(queue[i].Action == original[i].Action && queue[i].Owner == ActionOwner.Player,
                        "Append retains earlier player entry " + i);
                Check(queue[players].Activity.Code == f.Office.ActivityCode
                    && queue[players].Owner == ActionOwner.Player && queue[players].Source == CommandSource.PlayerHuman,
                    "Append adds normal PlayerHuman entry");
            }
        }
    }
    private static void ModeApplied(Fixture f, QueueMode mode)
    {
        ResidentPanelSnapshot view = f.View();
        ActionEntry entry = mode == QueueMode.Immediate ? view.Current
            : view.Queue.Count == 1 ? view.Queue[0] : null;
        Check(entry != null && entry.Activity.Code == f.Office.ActivityCode
            && entry.TargetId == (long)f.Office.Object && entry.Owner == ActionOwner.Player
            && entry.Source == CommandSource.PlayerHuman, "input preserves activity, target, source and mode " + mode);
        Check(mode == QueueMode.Immediate ? view.Queue.Count == 0 : view.Current == null,
            "input preserves Immediate versus Append " + mode);
    }
    private static List<HudElement> Elements(object menu)
    {
        var result = new List<HudElement>();
        foreach (HudElement e in (IEnumerable)Call(menu, "Elements", 1920, 1080)) result.Add(e);
        return result;
    }
    private static void MenuAccessibility()
    {
        foreach (float scale in new[] { 1f, 1.5f })
        {
            var f = new Fixture();
            Check(f.Access.TrySetTextScale(scale) && f.Access.TryRemap("SelectNext", "F8")
                && f.Access.TryRemap("SelectPrevious", "F9") && f.Access.TryRemap("Confirm", "F10"), "real accessible settings");
            object menu = f.Menu();
            List<HudElement> elements = Elements(menu);
            var focus = elements.FindAll(e => e.Focusable);
            focus.Sort((a, b) => a.ReadingOrder.CompareTo(b.ReadingOrder));
            Check(focus.Count == 2, "Immediate and Append separately reachable without modifier");
            var ids = new HashSet<string>();
            for (int i = 0; i < focus.Count; i++)
            {
                HudElement e = focus[i];
                Check(ids.Add(e.Id) && !string.IsNullOrWhiteSpace(e.Label) && e.Width >= 44f && e.Height >= 44f
                    && e.X >= 0f && e.Y >= 0f && e.X + e.Width <= 1920f && e.Y + e.Height <= 1080f,
                    "unique labelled >=44px on-screen target at scale " + scale);
                Check(e.HasPointerPath && (string.IsNullOrWhiteSpace(e.HoverInfo) || (e.RevealOnClick && e.RevealOnFocus)),
                    "pointer route and hover information on click/focus");
                if (i > 0) Check(focus[i - 1].ReadingOrder < e.ReadingOrder, "strict reading order");
                Check((bool)Call(menu, "TryInput", "F8") && Read<string>(menu, "FocusedElementId") == e.Id,
                    "keyboard visits every focusable in declared order");
            }
            Check((bool)Call(menu, "TryInput", "F8") && Read<string>(menu, "FocusedElementId") == focus[0].Id, "focus wraps");
            Check((bool)Call(menu, "TryInput", "F9") && Read<string>(menu, "FocusedElementId") == focus[1].Id, "reverse focus");
            Check(ids.Contains(Choice(new ActivityId(f.Office.ActivityCode), QueueMode.Immediate))
                && ids.Contains(Choice(new ActivityId(f.Office.ActivityCode), QueueMode.Append)), "both explicit mode choices");
            foreach (QueueMode mode in new[] { QueueMode.Immediate, QueueMode.Append })
            {
                var mouse = new Fixture();
                object pointerMenu = mouse.Menu();
                Check((bool)Call(pointerMenu, "TryClick", Choice(new ActivityId(mouse.Office.ActivityCode), mode))
                    && mouse.Commands.CommandLog.Count == 1 && mouse.Commands.CommandLog[0].Accepted,
                    "mouse-only choice " + mode);
                ModeApplied(mouse, mode);
                var keyboard = new Fixture();
                object keyboardMenu = keyboard.Menu();
                var keyboardChoices = Elements(keyboardMenu).FindAll(e => e.Focusable);
                keyboardChoices.Sort((a, b) => a.ReadingOrder.CompareTo(b.ReadingOrder));
                foreach (HudElement e in keyboardChoices)
                {
                    Check((bool)Call(keyboardMenu, "TryInput", keyboard.Access.BindingFor("SelectNext")), "keyboard next choice");
                    if (e.Id == Choice(new ActivityId(keyboard.Office.ActivityCode), mode)) break;
                }
                Check((bool)Call(keyboardMenu, "TryInput", keyboard.Access.BindingFor("Confirm"))
                    && keyboard.Commands.CommandLog.Count == 1, "keyboard-only choice " + mode);
                ModeApplied(keyboard, mode);
            }
            int logs = f.Commands.CommandLog.Count;
            Check((bool)Call(menu, "TryInput", "F10") && f.Commands.CommandLog.Count == logs + 1
                && f.Commands.CommandLog[logs].Accepted, "keyboard-only Confirm issues one normal command");
        }
    }
    private static void CursorTable()
    {
        var access = new Accessibility(new SettingsRecord(1, 1f, 1f, false));
        object cursor = New("CursorState", access);
        var shapes = new Dictionary<string, string>();
        // Exhaustive simultaneous-condition table, independently from SDoc priority.
        for (int flags = 0; flags < 32; flags++)
        {
            bool interactable = (flags & 1) != 0, resident = (flags & 2) != 0;
            bool blocked = (flags & 4) != 0, busy = (flags & 8) != 0, placement = (flags & 16) != 0;
            RejectReason? reason = blocked ? (RejectReason?)RejectReason.NoPath : null;
            string expected = busy ? "Busy" : blocked ? "Blocked" : placement ? "Placement"
                : resident ? "HoverResident" : interactable ? "HoverInteractable" : "Default";
            object cue = Call(cursor, "Resolve", interactable, resident, reason, busy, placement);
            string shape = Read<string>(cue, "ShapeId"), label = Read<string>(cue, "Label");
            Check(Read<object>(cue, "Kind").ToString() == expected && !string.IsNullOrWhiteSpace(shape)
                && !string.IsNullOrWhiteSpace(label), "cursor table row " + flags + " -> " + expected);
            if (expected == "Blocked") Check(label.Contains("NoPath"), "blocked cursor shows existing reason");
            if (shapes.ContainsKey(expected)) Check(shapes[expected] == shape, "stable non-color cue " + expected);
            else shapes.Add(expected, shape);
        }
        Check(shapes.Count == 6 && new HashSet<string>(shapes.Values).Count == 6, "six distinct non-color cursor shapes");
        object done = Call(cursor, "Resolve", true, false, null, false, false);
        Check(Read<object>(done, "Kind").ToString() == "HoverInteractable", "completion clears busy/blocked and refreshes hover");
    }
    public static int Main(string[] args)
    {
        failures = 0;
        negativeFixture = Array.IndexOf(args, "negative-fixture") >= 0;
        Run("real-producers.SEIT-SIM-04-N2.negative-oracle", RealProducersAndNegativeOracle);
        if (Array.IndexOf(args, "producer-check") >= 0)
        {
            if (failures == 0) Console.WriteLine("SELECT-ACTION PRODUCERS PASS");
            return failures == 0 ? 0 : 1;
        }
        Run("SelectionAndFocus.A3.SEIT-UI-02-P1", SelectionAndFocus);
        Run("SnapGridAndTie.A8.SEIT-UI-02-P1", SnapGridAndTie);
        Run("KeyboardAndTiming.A3.A8", KeyboardAndTiming);
        Run("ObjectMenu.SEIT-IF-CMD-P1", ObjectMenu);
        Run("CommandModes.SEIT-IF-CMD-P1.P2.SEIT-UI-02-P1", CommandModes);
        Run("AppendPolicyAndBound.SEIT-SIM-04-P2.N2", AppendPolicyAndBound);
        Run("MenuAccessibility.A3.A5", MenuAccessibility);
        Run("CursorTable.six-states", CursorTable);
        if (failures == 0) Console.WriteLine("SELECT-ACTION ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
