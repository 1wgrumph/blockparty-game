/*
HUD@1 test red contract. Plain C# 9; resource isolated-csharp.
Sources: plan.json HUD; m3/test-engineer.json HUD + A3/A5/A6/A7;
BP-UI-02 UI02-HUD (mood = active feeling kind/intensity, no new authority).
A separate module is required by the package selector; UI-INSPECT and ACCESS
remain unchanged. Real fixtures: WorldState, Clock, CommandArbitrator,
ResidentPanel, Accounts.Transfer and SaveV1.SaveSettings/LoadSettings. No mocks,
network, credentials, editor, display or global settings. Owned temp directory
is removed in finally. No new durable HUD state or literal save version oracle.

Planned public seam (reflection follows INPUT-ACCESS/ACCESS, so absent product
capabilities give named runtime FAILs rather than unrelated compiler errors):
 Blockparty.UI.Hud(WorldState, CommandArbitrator, Accessibility, InputMap)
   bool Select(ResidentId); object Snapshot(int width, int height)
   bool TryInput(string activeBinding); bool TryClick(string elementId)
   void ActionResult(ResidentId, ActivityId, CommandResult);
   void ActionCompleted(ResidentId, ActivityId); // real producer callbacks
   string FocusedElementId; Narration Narration
 Snapshot readable members: ResidentPanelSnapshot Panel;
   IEnumerable<ResidentId> Portraits; long GameSeconds, Balance;
   SimulationSpeed Speed; bool Paused; AgingPreset Aging; string MoodSummary;
   IEnumerable Elements, Palette.
 Element readable members: string Id, Label, ShapeId, HoverInfo;
   bool Focusable, HasPointerPath, RevealOnClick, RevealOnFocus;
   int ReadingOrder, ForegroundRgb, BackgroundRgb;
   float X,Y,Width,Height,BaseTextSize,TextSize.
 Palette pair: string Appearance; int ForegroundRgb, BackgroundRgb (0xRRGGBB).
 Stable element ids bind the future view: portrait:<ResidentId>, need:<Need>,
 mood, balance, clock, speed, result (after an action result),
 cancel:<ActionInstanceId>. ReadingOrder is the
 declared logical order. In InputMap state HudFocus, SelectNext/SelectPrevious
 traverse focus and Confirm activates; Tab is an active remap, not a raw key.
 Every focusable has a pointer path via TryClick. Cancel ids submit the existing
 ResidentPanel command contract; they never remove actions directly.
 Blockparty.UI.Narration(Accessibility): IEnumerable<string> Utterances;
   void Focus(string label); void Selection(ResidentRecord resident);
   void ActionResult(ResidentRecord, ActivityId, CommandResult);
   void ActionCompleted(ResidentRecord, ActivityId).
 Hud focus/portrait activation emits through that same Narration instance.

Trace: ProjectionAndSpend -> SEIT-UI-02-P1, SEIT-AC-12(local);
 Cancellation -> SEIT-UI-02-P2(local); Focus/Layout/Narration -> A3/A5/A7;
 PaletteAndScale/StatusCues -> A6, SEIT-PRES-05-N3(local).
 negative-fixture retains a REAL pre-spend HUD snapshot and applies the SAME
 balance oracle after a committed Accounts.Transfer; it must print FAIL and
 exit 1. The normal path refreshes that snapshot. ProducerAndOracle validates
 fixture production and the stale-balance detector independently of missing Hud.
 Model checks do not establish rendered contrast/rects, audible speech, a real
 process restart, or full system/acceptance SEIT completion (CONTROLS-PLAY).
*/
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;
using Blockparty.UI;

public static class HudChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private static int failures;
    private static bool negativeFixture;
    private static string root;

    private static void Check(bool ok, string reason)
    {
        if (!ok) throw new InvalidOperationException(reason);
    }
    private static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null)
                error = error.InnerException;
            failures++;
            Console.WriteLine("FAIL " + name + ": " + error.Message);
        }
    }
    private static Type Product(string name)
    {
        Type type = typeof(Accessibility).Assembly.GetType("Blockparty.UI." + name);
        Check(type != null, "missing HUD capability Blockparty.UI." + name);
        return type;
    }
    private static object Call(object instance, string name, params object[] args)
    {
        MethodInfo method = instance.GetType().GetMethod(name);
        Check(method != null, "missing HUD capability " + instance.GetType().Name + "." + name);
        return method.Invoke(instance, args);
    }
    private static T Read<T>(object instance, string name)
    {
        Check(instance != null, "null HUD projection for " + name);
        Type type = instance.GetType();
        PropertyInfo property = type.GetProperty(name);
        if (property != null) return (T)property.GetValue(instance);
        FieldInfo field = type.GetField(name);
        Check(field != null, "missing HUD capability " + type.Name + "." + name);
        return (T)field.GetValue(instance);
    }
    private static List<object> Items(object instance, string name)
    {
        var items = new List<object>();
        foreach (object item in Read<IEnumerable>(instance, name)) items.Add(item);
        return items;
    }
    private static object Element(object snapshot, string id)
    {
        foreach (object element in Items(snapshot, "Elements"))
            if (Read<string>(element, "Id") == id) return element;
        throw new InvalidOperationException("missing HUD element " + id);
    }
    private static string Id(string prefix, long value) { return prefix + value.ToString(CultureInfo.InvariantCulture); }
    private static bool Has(string text, string part)
    {
        return text != null && text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
    }
    private static string Cue(object element)
    {
        return Read<string>(element, "Label") + "|" + Read<string>(element, "ShapeId");
    }

    private sealed class Fixture
    {
        public readonly WorldState World;
        public readonly Clock Clock;
        public readonly CommandArbitrator Commands;
        public readonly Accounts Accounts;
        public readonly Accessibility Access;
        public readonly InputMap Input;
        public readonly ResidentRecord A, B, Outside;
        public object Hud;

        public Fixture()
        {
            World = WorldState.CreateSeeded(7L, 4242L, 0, null);
            HomeRecord home = World.AllocateHome(Core, 8);
            HomeRecord other = World.AllocateHome(Core, 8);
            HouseholdRecord house = World.AllocateHousehold(Core, "Controlled", home.Id);
            HouseholdRecord outsider = World.AllocateHousehold(Core, "Outside", other.Id);
            A = World.AllocateResident(Core, "Alex", LifeStage.Adult, house.Id, home.Id);
            B = World.AllocateResident(Core, "Blair", LifeStage.Adult, house.Id, home.Id);
            Outside = World.AllocateResident(Core, "Casey", LifeStage.Adult, outsider.Id, other.Id);
            World.SetMoney(Core, A.Id, 100L);
            World.SetMoney(Core, B.Id, 31L);
            for (int i = 0; i < StateSchema.NeedCount; i++)
                World.SetNeed(Core, A.Id, (Need)i, 11f + i * 9f);
            World.SetGameSeconds(Core, 3600L);
            Clock = new Clock(World);
            Commands = new CommandArbitrator(World, Clock);
            Accounts = new Accounts(World);
            Access = new Accessibility(new SettingsRecord(SaveV1.SettingsSchemaVersion, .7f, 1f, false));
            Access.SetNarrationEnabled(true);
            Input = new InputMap(Access, Clock);
            Input.EnterState("HudFocus");
        }
        public void CreateHud()
        {
            Hud = Activator.CreateInstance(Product("Hud"), new object[] { World, Commands, Access, Input });
            Check((bool)Call(Hud, "Select", A.Id), "HUD cannot select real resident");
        }
        public object Snapshot() { return Call(Hud, "Snapshot", 1920, 1080); }
        public CommandResult Direct(string code, QueueMode mode, CommandSource source = CommandSource.PlayerHuman)
        {
            if (source == CommandSource.PlayerAutomated) Commands.EnableAutomation();
            return Commands.Submit(new CommandEnvelope(World.AllocateCommand(Core), World.Epoch,
                World.GameSeconds, source, A.ResidentSequence + 1u, CommandKind.DirectActivity,
                A.Id, new ActivityId(code), null, new CommandParameters.Direct(mode)));
        }
        public void Queue()
        {
            Check(Direct("ACT-FOOD-01", QueueMode.Immediate).Accepted, "real current action refused");
            foreach (CommandSource source in new[] { CommandSource.PlayerHuman,
                CommandSource.PlayerAutomated, CommandSource.VoiceMapped })
                Check(Direct("ACT-EDU-02", QueueMode.Append, source).Accepted, "real queued action refused");
            Commands.EnableAutomation(); // Player/voice commands take control away from automation.
            AvailableAction offered = new AvailableAction(new ActivityId("ACT-SPORT-01"), null);
            Check(Commands.ApplyProposal(A.Id, new DecisionProposal((GatewayRequestId)1L,
                offered.Activity, null, Commands.CurrentStamp(A.Id)),
                new List<AvailableAction> {offered}.AsReadOnly()).Accepted, "real model proposal refused");
            Check(Commands.EnqueueAutonomous(A.Id, new ActivityId("ACT-SPORT-01"), null),
                "real autonomous queue refused");
        }
        public void Spend()
        {
            Check(Accounts.Transfer(Core, World.AllocateAction(Core), AccountRef.Personal(A.Id),
                AccountRef.Expense("Meal"), 27L).Accepted, "real spend refused");
            Check(A.Money == 73L && Accounts.Ledger.Count == 1 && Accounts.Ledger[0].Amount == 27L,
                "spend fixture did not commit");
        }
        public string Authority()
        {
            string text = World.ComputeStateHash() + ":" + World.RngState + ":" + World.WriteAudit.Count
                + ":" + Commands.CommandLog.Count + ":" + Accounts.Ledger.Count;
            foreach (ResidentRecord r in World.ResidentsSnapshot())
            {
                ActionEntry current = Commands.GetCurrent(r.Id);
                text += ":" + (current == null ? 0L : (long)current.Action);
                foreach (ActionEntry action in Commands.GetPending(r.Id)) text += "," + (long)action.Action;
            }
            return text;
        }
    }

    // Shared oracle for fresh HUD values, stale negative-fixture and its self-check.
    private static bool BalanceMatches(long shown, Fixture f) { return shown == f.A.Money; }
    private static void ProducerAndOracle()
    {
        Fixture f = new Fixture();
        f.Queue();
        ResidentPanelSnapshot old;
        string reason;
        Check(new ResidentPanel(f.World, f.Commands).TryInspect(f.A.Id, out old, out reason), reason);
        Check(f.Commands.Snapshot(f.A.Id).PlayerQueueHead == f.Commands.GetPending(f.A.Id)[0].Command,
            "real Snapshot queue-head oracle mismatch");
        Check(BalanceMatches(old.Resident.Money, f), "fresh producer balance rejected");
        f.Spend();
        Check(!BalanceMatches(old.Resident.Money, f), "stale balance oracle failed to detect committed spend");
        Check(BalanceMatches(73L, f), "fresh post-spend oracle rejected");
    }
    private static bool SameAction(ActionEntry a, ActionEntry b)
    {
        if (a == null || b == null) return a == b;
        return a.Action == b.Action && a.Command == b.Command && a.Kind == b.Kind
            && a.Activity == b.Activity && a.TargetId == b.TargetId && a.TargetKind == b.TargetKind
            && a.Owner == b.Owner && a.Source == b.Source;
    }
    private static void ProjectionAndSpend()
    {
        Fixture f = new Fixture();
        f.Queue();
        f.CreateHud();
        string before = f.Authority();
        object view = f.Snapshot();
        ResidentPanelSnapshot panel = Read<ResidentPanelSnapshot>(view, "Panel");
        ResidentRecord dump = f.World.ExportSnapshot().Residents.Find(r => r.Id == f.A.Id);
        Check(panel.Resident.Id == dump.Id && panel.Resident.DisplayName == dump.DisplayName,
            "selected identity/portrait differs from state dump");
        var portraits = new List<ResidentId>();
        foreach (ResidentId id in Read<IEnumerable>(view, "Portraits")) portraits.Add(id);
        Check(portraits.Count == 2 && portraits.Contains(f.A.Id) && portraits.Contains(f.B.Id)
            && !portraits.Contains(f.Outside.Id), "portraits differ from controlled household");
        for (int i = 0; i < StateSchema.NeedCount; i++)
        {
            Need need = (Need)i;
            Check(panel.Resident.GetNeed(need) == dump.GetNeed(need), "need differs from state dump: " + need);
            string label = Read<string>(Element(view, "need:" + need), "Label");
            Check(Has(label, need.ToString()) && Has(label, dump.GetNeed(need).ToString(CultureInfo.InvariantCulture)),
                "need lacks name/value cue: " + need);
        }
        Check(SameAction(panel.Current, f.Commands.GetCurrent(f.A.Id)), "current activity differs from authority");
        ActionEntry[] queue = f.Commands.GetPending(f.A.Id);
        Check(panel.Queue.Count == queue.Length, "queue count differs");
        for (int i = 0; i < queue.Length; i++)
            Check(SameAction(panel.Queue[i], queue[i]), "queue order/owner/player source differs: " + i);
        Check(panel.Queue[0].Command == f.Commands.Snapshot(f.A.Id).PlayerQueueHead,
            "queue differs from CommandArbitrator.Snapshot in same tick");
        Check(Read<long>(view, "GameSeconds") == f.World.GameSeconds && Read<SimulationSpeed>(view, "Speed") == f.Clock.Speed
            && Read<bool>(view, "Paused") == f.Clock.Paused && Read<AgingPreset>(view, "Aging") == f.Clock.Preset,
            "clock/speed/pause/aging differs from authority");
        Check(BalanceMatches(Read<long>(view, "Balance"), f), "initial balance differs from authority");
        foreach (string id in new[] { "balance", "clock", "speed", "mood" })
            Check(!string.IsNullOrWhiteSpace(Read<string>(Element(view, id), "Label")), "unlabelled overlay " + id);
        Check(f.Authority() == before, "snapshot wrote authoritative state, queue, log or RNG");
        f.Spend();
        // Only this choice changes in negative-fixture mode: the old real snapshot is stale.
        object after = negativeFixture ? view : f.Snapshot();
        Check(BalanceMatches(Read<long>(after, "Balance"), f), "stale balance after committed spend: expected 73");
        f.World.SetNeed(Core, f.A.Id, Need.Hunger, 91f);
        f.World.SetGameSeconds(Core, 7200L);
        f.Clock.SetSpeed(SimulationSpeed.Ultra);
        f.Clock.Pause();
        f.Clock.SetPreset(AgingPreset.Slow);
        object refreshed = f.Snapshot();
        Check(Read<ResidentPanelSnapshot>(refreshed, "Panel").Resident.GetNeed(Need.Hunger) == 91f
            && Read<long>(refreshed, "GameSeconds") == 7200L && Read<bool>(refreshed, "Paused")
            && Read<SimulationSpeed>(refreshed, "Speed") == SimulationSpeed.Ultra
            && Read<AgingPreset>(refreshed, "Aging") == AgingPreset.Slow, "snapshot did not refresh authority");
    }

    private static List<object> Focusables(object view)
    {
        var result = new List<object>();
        var ids = new HashSet<string>();
        foreach (object element in Items(view, "Elements"))
        {
            string id = Read<string>(element, "Id");
            Check(!string.IsNullOrWhiteSpace(id) && ids.Add(id), "empty/duplicate element id");
            if (Read<bool>(element, "Focusable")) result.Add(element);
        }
        result.Sort((a,b) => Read<int>(a,"ReadingOrder").CompareTo(Read<int>(b,"ReadingOrder")));
        Check(result.Count > 0, "no HUD focusables");
        for (int i = 1; i < result.Count; i++)
            Check(Read<int>(result[i-1], "ReadingOrder") < Read<int>(result[i], "ReadingOrder"), "ambiguous reading order");
        return result;
    }
    private static void FocusAndPointer()
    {
        Fixture f = new Fixture();
        f.Queue();
        Check(f.Access.TryRemap("SelectNext", "Tab") && f.Access.TryRemap("SelectPrevious", "F14")
            && f.Access.TryRemap("Confirm", "F15"), "fixture remaps refused");
        f.CreateHud();
        List<object> elements = Focusables(f.Snapshot());
        string before = f.Authority();
        var visited = new HashSet<string>();
        foreach (object element in elements)
        {
            Check((bool)Call(f.Hud, "TryInput", f.Input.HintFor("SelectNext")), "mapped focus activation refused");
            string focused = Read<string>(f.Hud, "FocusedElementId");
            Check(focused == Read<string>(element, "Id") && visited.Add(focused), "focus skipped/duplicated/inverted order");
            Check(!string.IsNullOrWhiteSpace(Read<string>(element, "Label")), "unlabelled focusable " + focused);
            Check(Read<bool>(element, "HasPointerPath"), "action has no pointer path " + focused);
        }
        Check((bool)Call(f.Hud,"TryInput",f.Input.HintFor("SelectNext"))
            && Read<string>(f.Hud,"FocusedElementId") == Read<string>(elements[0],"Id"), "focus does not wrap");
        Check((bool)Call(f.Hud,"TryInput",f.Input.HintFor("SelectPrevious"))
            && Read<string>(f.Hud,"FocusedElementId") == Read<string>(elements[elements.Count-1],"Id"), "reverse focus does not wrap");
        Check(f.Authority() == before, "focus navigation mutated authoritative state");
        Check(f.Access.TryRemap("SelectNext", "Mouse4"), "mouse remap refused");
        Check((bool)Call(f.Hud,"TryInput",f.Input.HintFor("SelectNext"))
            && Read<string>(f.Hud,"FocusedElementId") == Read<string>(elements[0],"Id"), "HUD ignored live mouse remap");
        Check(!(bool)Call(f.Hud,"TryInput","Tab"), "stale binding still drives HUD focus");
        foreach (object element in elements)
        {
            Fixture pointer = new Fixture();
            pointer.Queue();
            pointer.CreateHud();
            Check((bool)Call(pointer.Hud, "TryClick", Read<string>(element,"Id")), "pointer path refuses focusable");
        }
    }
    private static void Cancellation()
    {
        Fixture f = new Fixture();
        f.Queue();
        Check(f.Access.TryRemap("SelectNext", "F13") && f.Access.TryRemap("Confirm", "Mouse4"), "cancel remap refused");
        f.CreateHud();
        ActionEntry current = f.Commands.GetCurrent(f.A.Id), queued = f.Commands.GetPending(f.A.Id)[1];
        int log = f.Commands.CommandLog.Count, effects = f.World.EffectLedger.Count;
        Check((bool)Call(f.Hud,"TryClick",Id("cancel:",(long)queued.Action)), "queued pointer cancel refused");
        Check(f.Commands.CommandLog.Count == log+1 && f.Commands.CommandLog[log].Kind == CommandKind.CancelQueued
            && f.Commands.CommandLog[log].Accepted && SameAction(current,f.Commands.GetCurrent(f.A.Id)),
            "queued cancel did not submit normal CancelQueued command");
        foreach (ActionEntry action in f.Commands.GetPending(f.A.Id))
            Check(action.Action != queued.Action, "cancelled action remains queued");
        string target = Id("cancel:",(long)current.Action);
        bool reached = false;
        foreach (object unused in Focusables(f.Snapshot()))
        {
            Check((bool)Call(f.Hud,"TryInput",f.Input.HintFor("SelectNext")), "cancel focus route refused");
            if (Read<string>(f.Hud,"FocusedElementId") == target) { reached = true; break; }
        }
        Check(reached && (bool)Call(f.Hud,"TryInput",f.Input.HintFor("Confirm")), "mapped current cancel unreachable");
        Check(f.Commands.CommandLog.Count == log+2 && f.Commands.CommandLog[log+1].Kind == CommandKind.CancelCurrent
            && f.Commands.CommandLog[log+1].Accepted && f.Commands.GetCurrent(f.A.Id) == null
            && f.Commands.Interrupted.Count == 1 && f.Commands.Interrupted[0].Action == current.Action
            && f.World.EffectLedger.Count == effects, "current removed without command, or cancellation executed effects");
        ResidentPanelSnapshot view=Read<ResidentPanelSnapshot>(f.Snapshot(),"Panel");
        ActionEntry[] pending=f.Commands.GetPending(f.A.Id);
        Check(view.Current==null && view.Queue.Count==pending.Length,"HUD stale after cancellation");
        for (int i=0;i<pending.Length;i++) Check(SameAction(view.Queue[i],pending[i]),"HUD cancellation queue order differs");
    }

    private static void Layout()
    {
        Fixture f = new Fixture();
        f.Queue();
        f.CreateHud();
        foreach (float scale in new[] { 1f, 1.5f })
        {
            Check(f.Access.TrySetTextScale(scale), "text scale fixture refused");
            foreach (object element in Items(f.Snapshot(),"Elements"))
            {
                float x=Read<float>(element,"X"), y=Read<float>(element,"Y");
                float w=Read<float>(element,"Width"), h=Read<float>(element,"Height");
                Check(x>=0f && y>=0f && w>0f && h>0f && x+w<=1920f && y+h<=1080f,
                    "element clipped/outside 1080p at scale " + scale);
                if (Read<bool>(element,"Focusable")) Check(w>=44f && h>=44f,"target smaller than 44x44 at scale " + scale);
                if (!string.IsNullOrWhiteSpace(Read<string>(element,"HoverInfo")))
                    Check(Read<bool>(element,"RevealOnClick") && Read<bool>(element,"RevealOnFocus"), "hover-only information");
                float basis=Read<float>(element,"BaseTextSize"), size=Read<float>(element,"TextSize");
                Check(basis>0f && size==f.Access.ScaledTextSize(basis), "text size != base x TextScale");
            }
        }
    }
    private static double Linear(int channel)
    {
        double value=channel/255.0;
        return value<=0.04045 ? value/12.92 : Math.Pow((value+0.055)/1.055,2.4);
    }
    private static double Luminance(int rgb)
    {
        Check(rgb>=0 && rgb<=0xffffff,"invalid RGB palette value");
        return .2126*Linear((rgb>>16)&255)+.7152*Linear((rgb>>8)&255)+.0722*Linear(rgb&255);
    }
    private static void PaletteAndScale()
    {
        Check(Math.Abs((Luminance(0xffffff)+.05)/(Luminance(0)+.05)-21.0)<1e-10,
            "contrast oracle self-check failed");
        Fixture f = new Fixture();
        f.Queue();
        f.CreateHud();
        foreach (float scale in new[] { 1f,1.5f })
        {
            f.Access.TrySetTextScale(scale);
            var appearances=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            object view=f.Snapshot();
            var pairs=Items(view,"Palette");
            pairs.AddRange(Items(view,"Elements"));
            foreach (object pair in pairs)
            {
                if (pair.GetType().GetProperty("Appearance") != null || pair.GetType().GetField("Appearance") != null)
                    appearances.Add(Read<string>(pair,"Appearance"));
                double fg=Luminance(Read<int>(pair,"ForegroundRgb")), bg=Luminance(Read<int>(pair,"BackgroundRgb"));
                Check((Math.Max(fg,bg)+.05)/(Math.Min(fg,bg)+.05)>=4.5,"text/icon contrast below 4.5:1");
            }
            foreach (string state in new[] {"Enabled","Disabled","Focused","Selected","Blocked"})
                Check(appearances.Contains(state),"palette omits appearance " + state);
        }
    }
    private static void StatusCues()
    {
        foreach (bool enabled in new[] {true,false})
        {
            Fixture f = new Fixture();
            f.Access.SetNonColorCues(enabled);
            f.CreateHud();
            Check(Has(Read<string>(f.Snapshot(),"MoodSummary"),"No active feeling"),"empty mood lacks explicit cue");
            foreach (Need need in Enum.GetValues(typeof(Need)))
            {
                var cues=new HashSet<string>();
                foreach (float value in new[] {0f,50f,100f})
                {
                    f.World.SetNeed(Core,f.A.Id,need,value);
                    object element=Element(f.Snapshot(),"need:"+need);
                    Check(!string.IsNullOrWhiteSpace(Read<string>(element,"Label"))
                        || !string.IsNullOrWhiteSpace(Read<string>(element,"ShapeId")),"color-only need");
                    Check(cues.Add(Cue(element)),"need states indistinguishable without color, NonColorCues="+enabled);
                }
            }
            var resultCues=new HashSet<string>();
            ActivityId activity=new ActivityId("ACT-FOOD-01");
            CommandResult insufficient=f.Accounts.Transfer(Core,f.World.AllocateAction(Core),
                AccountRef.Personal(f.A.Id),AccountRef.Expense("Meal"),101L);
            CommandResult invalid=new ResidentPanel(f.World,f.Commands).Cancel(f.A.Id,(ActionInstanceId)999999L);
            Check(!insufficient.Accepted && insufficient.Reason==RejectReason.InsufficientFunds
                && !invalid.Accepted && invalid.Reason==RejectReason.InvalidParameters,"real blocked result fixtures failed");
            foreach (CommandResult result in new[] {insufficient,invalid,f.Direct("ACT-FOOD-01",QueueMode.Immediate)})
            {
                Call(f.Hud,"ActionResult",f.A.Id,activity,result);
                object element=Element(f.Snapshot(),"result");
                string label=Read<string>(element,"Label");
                Check(Has(label,activity.ToString()) && Has(label,result.Accepted?"Accepted":result.Reason.ToString()),
                    "blocked/accepted result lacks activity and outcome cue, NonColorCues="+enabled);
                Check(resultCues.Add(Cue(element)),"blocked/accepted states differ only by color");
            }
            Fixture mixture=new Fixture();
            mixture.Access.SetNonColorCues(enabled);
            WorldEvent mixedEvent=mixture.World.AppendEvent(Core,"MixedFeelings",mixture.A.Id,"observed");
            mixture.World.AddFeeling(Core,mixture.A.Id,FeelingKind.Grateful,mixedEvent.Event,mixture.B.Id,2,7200L);
            mixture.World.AddFeeling(Core,mixture.A.Id,FeelingKind.Angry,mixedEvent.Event,mixture.B.Id,3,7200L);
            mixture.World.AddFeeling(Core,mixture.A.Id,FeelingKind.Hurt,mixedEvent.Event,mixture.B.Id,1,3599L);
            mixture.CreateHud();
            string summary=Read<string>(mixture.Snapshot(),"MoodSummary");
            Check(Has(summary,"Grateful") && Has(summary,"2") && Has(summary,"Angry") && Has(summary,"3")
                && !Has(summary,"Hurt"),"mood does not list every active feeling or includes an expired one");
            var moods=new HashSet<string>();
            foreach (FeelingKind kind in Enum.GetValues(typeof(FeelingKind)))
                foreach (int intensity in new[] {1,2,3})
                {
                    // Fresh real world for each active feeling, never a replacement mood producer.
                    Fixture mood=new Fixture();
                    mood.Access.SetNonColorCues(enabled);
                    WorldEvent ev=mood.World.AppendEvent(Core,"FeelingFixture",mood.A.Id,"observed");
                    mood.World.AddFeeling(Core,mood.A.Id,kind,ev.Event,mood.B.Id,intensity,7200L);
                    mood.CreateHud();
                    object view=mood.Snapshot();
                    string text=Read<string>(view,"MoodSummary");
                    Check(Has(text,kind.ToString()) && Has(text,intensity.ToString()),"mood omits active kind/intensity");
                    Check(moods.Add(Cue(Element(view,"mood"))),"mood states differ only by color");
                    mood.World.SetGameSeconds(Core,7201L);
                    Check(Has(Read<string>(mood.Snapshot(),"MoodSummary"),"No active feeling"),"expired feeling shown as active mood");
                }
        }
    }

    private static List<string> Utterances(object narration)
    {
        var values=new List<string>();
        foreach (string text in Read<IEnumerable>(narration,"Utterances")) values.Add(text);
        return values;
    }
    private static void NewUtterance(object narration, int before, params string[] words)
    {
        List<string> values=Utterances(narration);
        Check(values.Count>before,"event emitted no utterance");
        string text=string.Join(" ",values.GetRange(before,values.Count-before));
        foreach (string word in words) Check(Has(text,word),"utterance omits " + word);
    }
    private static void NarrationEvents()
    {
        Fixture f=new Fixture();
        f.Queue();
        f.CreateHud();
        object narration=Read<object>(f.Hud,"Narration");
        foreach (object element in Focusables(f.Snapshot()))
        {
            int before=Utterances(narration).Count;
            Check((bool)Call(f.Hud,"TryInput",f.Input.HintFor("SelectNext")),"narrated focus refused");
            NewUtterance(narration,before,Read<string>(element,"Label"));
        }
        int start=Utterances(narration).Count;
        Check((bool)Call(f.Hud,"TryClick",Id("portrait:",(long)f.B.Id)),"portrait pointer selection refused");
        Check(Read<ResidentPanelSnapshot>(f.Snapshot(),"Panel").Resident.Id==f.B.Id,"portrait did not select identity");
        NewUtterance(narration,start,f.B.DisplayName);
        Check((bool)Call(f.Hud,"Select",f.A.Id),"selection restore refused");
        ActivityId activity=new ActivityId("ACT-FOOD-01");
        CommandResult accepted=f.Direct("ACT-FOOD-01",QueueMode.Append);
        Check(accepted.Accepted,"real accepted result fixture refused");
        start=Utterances(narration).Count;
        Call(f.Hud,"ActionResult",f.A.Id,activity,accepted);
        NewUtterance(narration,start,f.A.DisplayName,activity.ToString(),"Accepted");
        CommandResult refused=f.Accounts.Transfer(Core,f.World.AllocateAction(Core),AccountRef.Personal(f.A.Id),
            AccountRef.Expense("Meal"),101L);
        Check(!refused.Accepted && refused.Reason==RejectReason.InsufficientFunds,"real refusal fixture failed");
        start=Utterances(narration).Count;
        Call(f.Hud,"ActionResult",f.A.Id,activity,refused);
        NewUtterance(narration,start,f.A.DisplayName,activity.ToString(),refused.Reason.ToString());
        ActionEntry current=f.Commands.GetCurrent(f.A.Id);
        Check(f.Commands.CompleteCurrent(f.A.Id,current.Action),"real completion refused");
        start=Utterances(narration).Count;
        Call(f.Hud,"ActionCompleted",f.A.Id,current.Activity);
        NewUtterance(narration,start,f.A.DisplayName,current.Activity.ToString(),"Completed");
        f.Access.SetNarrationEnabled(false);
        start=Utterances(narration).Count;
        Call(narration,"Focus","Balance");
        Call(narration,"Selection",f.A);
        Call(f.Hud,"ActionResult",f.A.Id,activity,accepted);
        Call(f.Hud,"ActionCompleted",f.A.Id,activity);
        Check(Utterances(narration).Count==start,"disabled narration emitted speech events");
    }
    private static void NarrationPersists()
    {
        foreach (bool enabled in new[] {true,false})
        {
            Fixture f=new Fixture();
            f.Access.SetNarrationEnabled(enabled);
            string directory=Path.Combine(root,enabled?"enabled":"disabled");
            Directory.CreateDirectory(directory);
            Check(SaveV1.SaveSettings(f.Access.Settings,directory).Ok,"real SaveSettings refused");
            SettingsLoadResult load=SaveV1.LoadSettings(directory);
            Check(load.Ok && load.Settings.NarrationEnabled==enabled,"narration setting lost on public save roundtrip");
            var access=new Accessibility(load.Settings);
            object narration=Activator.CreateInstance(Product("Narration"),new object[] {access});
            int before=Utterances(narration).Count;
            Call(narration,"Focus","Balance");
            if (enabled) NewUtterance(narration,before,"Balance");
            else Check(Utterances(narration).Count==before,"reloaded disabled narration ignored");
        }
    }
    private static void NoWriter()
    {
        foreach (string name in new[] {"Hud","Narration"})
        {
            Type type=Product(name);
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic))
                Check(field.FieldType!=typeof(StateWriter) && Nullable.GetUnderlyingType(field.FieldType)!=typeof(StateWriter),
                    name+" holds StateWriter "+field.Name);
            foreach (ConstructorInfo ctor in type.GetConstructors())
                foreach (ParameterInfo arg in ctor.GetParameters())
                    Check(arg.ParameterType!=typeof(StateWriter),name+" accepts StateWriter");
        }
    }
    public static int Main(string[] args)
    {
        negativeFixture=Array.IndexOf(args,"negative-fixture")>=0;
        root=Path.Combine(Path.GetTempPath(),"blockparty-HUD-"+Process.GetCurrentProcess().Id+"-"+Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            if (negativeFixture) Console.WriteLine("HUD negative-fixture: retain real pre-spend snapshot");
            Run("ProducerAndOracle",ProducerAndOracle);
            Run("ProjectionAndSpend [SEIT-UI-02-P1;SEIT-AC-12 local]",ProjectionAndSpend);
            Run("NoWriter [sole-writer]",NoWriter);
            Run("FocusAndPointer [A3]",FocusAndPointer);
            Run("Cancellation [SEIT-UI-02-P2 local]",Cancellation);
            Run("Layout [A5;A6 text scale]",Layout);
            Run("PaletteAndScale [A6 contrast]",PaletteAndScale);
            Run("StatusCues [A6;SEIT-PRES-05-N3 local]",StatusCues);
            Run("NarrationEvents [A7]",NarrationEvents);
            Run("NarrationPersists [A7 public settings save]",NarrationPersists);
        }
        catch (Exception error) { failures++; Console.WriteLine("FAIL HUD setup: "+error.Message); }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root,true); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL HUD owned-temp cleanup: "+error.Message); }
        }
        if (failures==0) Console.WriteLine("HUD ALL PASS");
        return failures==0 ? 0 : 1;
    }
}
