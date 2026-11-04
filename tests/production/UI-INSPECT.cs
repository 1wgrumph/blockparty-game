/* UI-INSPECT@1 TDD red contract. Product file (plan writes):
 * Assets/Blockparty/UI/ResidentPanel.cs; namespace Blockparty.UI; plain C# 9.
 * Required public API:
 *   sealed class ResidentPanel {
 *     ResidentPanel(WorldState world, CommandArbitrator arbitrator);
 *     bool TryInspect(ResidentId resident, out ResidentPanelSnapshot snapshot,
 *                     out string reason);
 *     bool TryExplain(ResidentId resident, EventId sourceEvent,
 *                     out MemoryEntry explanation, out string reason);
 *     CommandResult Cancel(ResidentId resident, ActionInstanceId action);
 *   }
 *   sealed class ResidentPanelSnapshot {
 *     ResidentRecord Resident { get; } // detached, public read-only projection
 *     ActionEntry Current { get; } // null when idle
 *     IReadOnlyList<ActionEntry> Queue { get; } // detached pending order
 *   }
 * Snapshot members may be readonly fields or getters without public setters.
 * Reuse Blockparty.Simulation records (including needs, traits, feelings and
 * Memories), and Blockparty.Contracts IDs/CommandResult. Resident.Id is the
 * portrait binding key; Home supplies location context. No UnityEngine here.
 * TryInspect refreshes from authority by ResidentId, including retained dead
 * residents. TryExplain returns a detached factual memory joined to an existing
 * event; preserve original participants/time/place/outcome and LearnedBy.
 * Unknown resident, missing event or event not known to that resident returns
 * false, null output and a nonblank reason without state/RNG/queue changes.
 * Cancel resolves the selected ActionInstanceId and submits a PlayerHuman
 * CancelCurrent or CancelQueued command through CommandArbitrator (queued
 * cancellation targets the entry's CommandId, not its ActionInstanceId).
 * An unknown/stale action or resident returns a rejected CommandResult with a
 * typed reason and no authoritative changes. No panel path writes world data.
 *
 * Scope: local oracle slices of the plan's traceability cases, NOT whole SEIT
 * completion. Needs/personality/feelings/history are read-only projections;
 * only Cancel is a command. No new fixture files, config, exports or callers:
 * run-standalone.py discovers this required package module automatically.
 * Real inputs come from seeded WorldState, Relationships and CommandArbitrator;
 * no mocks, fake producers, engine, display, ports or external I/O. Resource
 * group isolated-csharp. Snapshot reload below proves local history rebind,
 * not SaveV1 disk persistence/process restart or notification persistence.
 * Pending gates: SEIT-UI-04-P1/N1 (four notification classes/dismissal and
 * focus), UI-04-P2 and AC-22 (uncoached players). UI-04-N2/N3 and IF-RESOLVE
 * cover only history/identity model slices; rendered focus remains GREYBOX.
 * AC-06/12, REL-05-P1 and UI-02-P1 still need packaged UI demonstrations;
 * personality recognition, portraits, camera, search/follow and the full
 * relationship/move/reload acceptance matrix are not claimed by this suite.
 * UI_INSPECT_PRODUCER_CHECK compiles only real fixture producers as a separate
 * preflight. Its success marker never satisfies the UI-INSPECT suite gate.
 */
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Blockparty.Contracts;
using Blockparty.Simulation;
#if !UI_INSPECT_PRODUCER_CHECK
using Blockparty.UI;
#endif

public static class UiInspectChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private static int failures;

    private static void Check(bool condition, string name)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
        if (!condition) failures++;
    }

    private sealed class Fixture
    {
        public readonly WorldState World;
        public readonly CommandArbitrator Commands;
        public readonly Relationships Relations;
#if !UI_INSPECT_PRODUCER_CHECK
        public readonly ResidentPanel Panel;
#endif
        public readonly ResidentRecord A;
        public readonly ResidentRecord B;
        public readonly ResidentRecord C;

        public Fixture(long seed = 4242L)
        {
            World = WorldState.CreateSeeded(7L, seed, 0, null);
            HomeRecord first = World.AllocateHome(Core, 8);
            HomeRecord second = World.AllocateHome(Core, 8);
            HouseholdRecord h1 = World.AllocateHousehold(Core, "First", first.Id);
            HouseholdRecord h2 = World.AllocateHousehold(Core, "Second", second.Id);
            A = World.AllocateResident(Core, "Sam", LifeStage.Adult, h1.Id, first.Id);
            B = World.AllocateResident(Core, "Sam", LifeStage.Adult, h2.Id, second.Id);
            C = World.AllocateResident(Core, "Listener", LifeStage.Adult, h1.Id, first.Id);
            for (int i = 0; i < StateSchema.NeedCount; i++)
                World.SetNeed(Core, A.Id, (Need)i, 11.0f + (float)i * 9.0f);
            for (int i = 0; i < StateSchema.TraitCount; i++)
                World.SetTrait(Core, A.Id, (TraitAxis)i, -35 + i * 13);
            World.SetTrait(Core, B.Id, TraitAxis.Sociable, 0);
            World.SetBond(Core, A.Id, B.Id, 10.0f, 25.0f);
            World.SetBond(Core, B.Id, A.Id, 10.0f, 25.0f);
            Commands = new CommandArbitrator(World, new Clock(World));
            Relations = new Relationships(World);
#if !UI_INSPECT_PRODUCER_CHECK
            Panel = new ResidentPanel(World, Commands);
#endif
        }

        public CommandResult Direct(string code, QueueMode mode,
            CommandSource source = CommandSource.PlayerHuman)
        {
            if (source == CommandSource.PlayerAutomated) Commands.EnableAutomation();
            return Commands.Submit(new CommandEnvelope(World.AllocateCommand(Core),
                World.Epoch, World.GameSeconds, source, A.ResidentSequence + 1u,
                CommandKind.DirectActivity, A.Id, new ActivityId(code), null,
                new CommandParameters.Direct(mode)));
        }

        public EventId Argument()
        {
            World.SetGameSeconds(Core, 5L * 86400L + 8L * 3600L);
            RelationshipResult result = Relations.Complete(Core,
                new ActivityId("ACT-REL-04"), A.Id, B.Id, 0.0, 0.9, 0.99, 0.01);
            if (!result.Applied || B.Feelings.Count == 0)
                throw new InvalidOperationException("Real argument fixture failed: " + result.Reason);
            return result.Event;
        }
    }

#if !UI_INSPECT_PRODUCER_CHECK
    private static ResidentPanelSnapshot Inspect(ResidentPanel panel, ResidentId resident)
    {
        ResidentPanelSnapshot value;
        string reason;
        if (!panel.TryInspect(resident, out value, out reason) || value == null)
            throw new InvalidOperationException("TryInspect refused fixture: " + reason);
        return value;
    }

    private static bool SameAction(ActionEntry left, ActionEntry right)
    {
        if (left == null || right == null) return left == right;
        return left.Action == right.Action && left.Command == right.Command
            && left.Kind == right.Kind && left.Activity == right.Activity
            && left.TargetId == right.TargetId && left.Owner == right.Owner
            && left.Source == right.Source;
    }

    private static bool QueueMatches(Fixture f, ResidentPanelSnapshot view)
    {
        ActionEntry[] queue = f.Commands.GetPending(f.A.Id);
        if (!SameAction(view.Current, f.Commands.GetCurrent(f.A.Id))
            || view.Queue.Count != queue.Length) return false;
        for (int i = 0; i < queue.Length; i++)
            if (!SameAction(view.Queue[i], queue[i])) return false;
        return true;
    }

    private static bool SameMemory(MemoryEntry left, MemoryEntry right)
    {
        if (left == null || right == null) return false;
        if (left.Event != right.Event || left.Kind != right.Kind
            || left.GameSeconds != right.GameSeconds || left.Place != right.Place
            || left.OutcomeCode != right.OutcomeCode || left.LearnedBy != right.LearnedBy
            || left.Participants.Count != right.Participants.Count) return false;
        for (int i = 0; i < left.Participants.Count; i++)
            if (left.Participants[i] != right.Participants[i]) return false;
        return true;
    }

    private static string Authority(Fixture f)
    {
        StringBuilder text = new StringBuilder();
        text.Append(f.World.ComputeStateHash()).Append('|').Append(f.World.RngState)
            .Append('|').Append(f.World.WriteAudit.Count).Append('|')
            .Append(f.Commands.CommandLog.Count).Append('|').Append(f.Commands.Interrupted.Count)
            .Append('|').Append(f.Commands.AutomationEnabled);
        foreach (ResidentRecord resident in f.World.ResidentsSnapshot())
        {
            text.Append('|').Append((long)resident.Id);
            ActionEntry current = f.Commands.GetCurrent(resident.Id);
            text.Append(':').Append(current == null ? 0L : (long)current.Action);
            foreach (ActionEntry action in f.Commands.GetPending(resident.Id))
                text.Append(',').Append((long)action.Action);
        }
        return text.ToString();
    }

    private static void ProjectionAndRefresh()
    {
        Fixture f = new Fixture();
        Check(f.Direct("ACT-FOOD-01", QueueMode.Immediate).Accepted
            && f.Direct("ACT-EDU-02", QueueMode.Append).Accepted,
            "SEIT-UI-02-P1.real-command-fixture");
        string before = Authority(f);
        ResidentPanelSnapshot view = Inspect(f.Panel, f.A.Id);
        bool values = view.Resident.Id == f.A.Id && view.Resident.DisplayName == "Sam"
            && view.Resident.Home == f.A.Home && view.Resident.IsAlive;
        for (int i = 0; i < 8; i++)
            values &= view.Resident.GetNeed((Need)i) == 11.0f + (float)i * 9.0f;
        for (int i = 0; i < 6; i++)
            values &= view.Resident.GetTrait((TraitAxis)i) == -35 + i * 13;
        IList<ActionEntry> exposedQueue = view.Queue as IList<ActionEntry>;
        Check(exposedQueue == null || exposedQueue.IsReadOnly,
            "SEIT-UI-02-P1.queue-projection-is-read-only");
        Check(values && QueueMatches(f, view),
            "SEIT-UI-02-P1.SEIT-AC-12.eight-needs-six-traits-identity-current-queue");
        Check(Authority(f) == before,
            "SEIT-UI-02-P1.inspection-does-not-write-state-or-consume-rng");
        f.World.SetNeed(Core, f.A.Id, Need.Hunger, 91.0f);
        f.World.SetTrait(Core, f.A.Id, TraitAxis.Sociable, 49);
        f.World.RenameResident(Core, f.A.Id, "Renamed");
        Check(view.Resident.GetNeed(Need.Hunger) == 11.0f
            && view.Resident.GetTrait(TraitAxis.Sociable) == -35
            && view.Resident.DisplayName == "Sam",
            "SEIT-UI-02-N1.old-projection-is-detached");
        ResidentPanelSnapshot refreshed = Inspect(f.Panel, f.A.Id);
        Check(refreshed.Resident.GetNeed(Need.Hunger) == 91.0f
            && refreshed.Resident.GetTrait(TraitAxis.Sociable) == 49
            && refreshed.Resident.DisplayName == "Renamed",
            "SEIT-UI-02-N1.refresh-reads-current-authority");
        for (int i = 0; i < 20; i++)
        {
            bool mutated;
            switch (i % 4)
            {
                case 0: mutated = f.Direct("ACT-FOOD-01", QueueMode.Immediate).Accepted; break;
                case 1: mutated = f.Direct("ACT-EDU-02", QueueMode.Append).Accepted; break;
                case 2: mutated = f.Commands.CompleteCurrent(f.A.Id,
                    f.Commands.GetCurrent(f.A.Id).Action); break;
                default: mutated = f.Direct("ACT-SPORT-01", QueueMode.Append).Accepted; break;
            }
            Check(mutated && QueueMatches(f, Inspect(f.Panel, f.A.Id)),
                "SEIT-UI-02-N1.queue-refresh-mutation-" + i);
        }
    }

    private static void OwnershipAndCancellation()
    {
        Fixture f = new Fixture();
        Check(f.Direct("ACT-FOOD-01", QueueMode.Immediate).Accepted,
            "SEIT-UI-02-N2.real-current-fixture");
        foreach (CommandSource source in new[] { CommandSource.PlayerHuman,
            CommandSource.PlayerAutomated, CommandSource.VoiceMapped })
            Check(f.Direct("ACT-EDU-02", QueueMode.Append, source).Accepted,
                "SEIT-UI-02-N2.real-player-source-" + source);
        ResidentPanelSnapshot view = Inspect(f.Panel, f.A.Id);
        Check(view.Queue.Count == 3 && view.Queue[0].Owner == ActionOwner.Player
            && view.Queue[1].Owner == ActionOwner.Player && view.Queue[2].Owner == ActionOwner.Player
            && view.Queue[0].Source == CommandSource.PlayerHuman
            && view.Queue[1].Source == CommandSource.PlayerAutomated
            && view.Queue[2].Source == CommandSource.VoiceMapped,
            "SEIT-UI-02-N2.three-distinct-sources-one-player-owner");
        Check(f.Commands.EnqueueAutonomous(f.A.Id, new ActivityId("ACT-SPORT-01"), null)
            && QueueMatches(f, Inspect(f.Panel, f.A.Id))
            && Inspect(f.Panel, f.A.Id).Queue[3].Owner == ActionOwner.Autonomous
            && !Inspect(f.Panel, f.A.Id).Queue[3].Source.HasValue,
            "SEIT-UI-02-P1.autonomous-owner-has-no-player-source");
        f.Commands.EnableAutomation();
        AvailableAction offered = new AvailableAction(new ActivityId("ACT-SPORT-01"), null);
        CommandResult model = f.Commands.ApplyProposal(f.A.Id,
            new DecisionProposal((GatewayRequestId)1L, offered.Activity, null,
                f.Commands.CurrentStamp(f.A.Id)), new List<AvailableAction> { offered }.AsReadOnly());
        view = Inspect(f.Panel, f.A.Id);
        Check(model.Accepted && QueueMatches(f, view) && view.Queue.Count == 4
            && view.Queue[3].Owner == ActionOwner.ModelProposed && !view.Queue[3].Source.HasValue,
            "SEIT-UI-02-P1.model-owner-preserved-after-real-proposal");
        ActionEntry cancelled = view.Queue[1];
        ActionEntry current = view.Current;
        int effects = f.World.EffectLedger.Count;
        int logs = f.Commands.CommandLog.Count;
        Check(f.Panel.Cancel(f.A.Id, cancelled.Action).Accepted
            && f.Commands.CommandLog.Count == logs + 1
            && f.Commands.CommandLog[logs].Kind == CommandKind.CancelQueued
            && f.Commands.CommandLog[logs].Resident == f.A.Id
            && f.Commands.CommandLog[logs].Accepted
            && SameAction(current, f.Commands.GetCurrent(f.A.Id))
            && f.Commands.GetPending(f.A.Id).Length == 3
            && f.Commands.GetPending(f.A.Id)[0].Action == view.Queue[0].Action
            && f.Commands.GetPending(f.A.Id)[1].Action == view.Queue[2].Action
            && f.Commands.GetPending(f.A.Id)[2].Action == view.Queue[3].Action
            && f.World.EffectLedger.Count == effects && QueueMatches(f, Inspect(f.Panel, f.A.Id)),
            "SEIT-UI-02-P2.cancel-queued-command-preserves-order-without-effects");
        Check(view.Queue.Count == 4 && view.Queue[1].Action == cancelled.Action,
            "SEIT-UI-02-N1.queue-projection-does-not-change-after-cancel");
        logs = f.Commands.CommandLog.Count;
        int interruptions = f.Commands.Interrupted.Count;
        Check(f.Panel.Cancel(f.A.Id, current.Action).Accepted
            && f.Commands.CommandLog.Count == logs + 1
            && f.Commands.CommandLog[logs].Kind == CommandKind.CancelCurrent
            && f.Commands.CommandLog[logs].Resident == f.A.Id
            && f.Commands.CommandLog[logs].Accepted
            && f.Commands.Interrupted.Count == interruptions + 1
            && f.Commands.Interrupted[interruptions].Action == current.Action
            && f.Commands.GetCurrent(f.A.Id) == null && f.World.EffectLedger.Count == effects
            && QueueMatches(f, Inspect(f.Panel, f.A.Id)),
            "SEIT-UI-02-P2.cancel-current-is-logged-command-and-interruption");
        string before = Authority(f);
        CommandResult stale = f.Panel.Cancel(f.A.Id, current.Action);
        Check(!stale.Accepted && stale.Reason == RejectReason.InvalidParameters
            && Authority(f) == before,
            "SEIT-UI-02-P2.stale-cancel-refused-with-reason-and-state-unchanged");
    }

    private static void FactualExplanations()
    {
        Fixture f = new Fixture();
        EventId source = f.Argument();
        WorldEvent recorded = null;
        foreach (WorldEvent entry in f.World.Events) if (entry.Event == source) recorded = entry;
        string before = Authority(f);
        MemoryEntry explanation;
        string reason;
        bool found = f.Panel.TryExplain(f.B.Id, source, out explanation, out reason);
        ResidentRecord shown = Inspect(f.Panel, f.B.Id).Resident;
        Check(found && recorded != null && explanation != null
            && explanation.Event == source && explanation.Kind == "argument"
            && explanation.Kind == recorded.Kind && explanation.GameSeconds == recorded.GameSeconds
            && explanation.GameSeconds == 5L * 86400L + 8L * 3600L
            && explanation.Participants.Count == 2 && explanation.Participants[0] == f.B.Id
            && explanation.Participants[1] == f.A.Id && explanation.LearnedBy == "participant"
            && SameMemory(explanation, f.Relations.Explain(f.B.Id, source))
            && shown.Feelings.Count == 1 && shown.Feelings[0].Kind == FeelingKind.Hurt
            && shown.Feelings[0].SourceEvent == source && shown.Feelings[0].Other == f.A.Id
            && shown.Feelings[0].Intensity == f.B.Feelings[0].Intensity
            && shown.Feelings[0].ExpiryGameSeconds == f.B.Feelings[0].ExpiryGameSeconds
            && shown.Memories.Count == f.B.Memories.Count && Authority(f) == before,
            "SEIT-REL-05-P1.SEIT-AC-06.SEIT-AC-12.feeling-links-real-event-participants-time");
        IList<ResidentId> participants = explanation == null ? null : explanation.Participants as IList<ResidentId>;
        Check(explanation != null && (participants == null || participants.IsReadOnly),
            "SEIT-REL-05-N1.explanation-participants-are-read-only");
        MemoryEntry original = explanation;
        f.World.SetGameSeconds(Core, f.World.GameSeconds + 60L);
        RelationshipResult chat = f.Relations.Complete(Core, new ActivityId("ACT-REL-01"), f.B.Id, f.C.Id);
        Check(chat.Applied && f.Panel.TryExplain(f.C.Id, source, out explanation, out reason)
            && explanation != null && explanation.LearnedBy == "told-by:" + ((long)chat.Event).ToString()
            && explanation.Participants.Count == 2 && explanation.Participants[0] == f.B.Id
            && explanation.Participants[1] == f.A.Id && explanation.GameSeconds == original.GameSeconds
            && explanation.Place == original.Place && explanation.OutcomeCode == original.OutcomeCode,
            "SEIT-REL-05-N1.chat-learned-history-does-not-invent-participation");
        f.World.SetGameSeconds(Core, shown.Feelings[0].ExpiryGameSeconds);
        f.Relations.Tick(Core);
        Check(Inspect(f.Panel, f.B.Id).Resident.Feelings.Count == 0
            && shown.Feelings.Count == 1 && f.Panel.TryExplain(f.B.Id, source, out explanation, out reason)
            && SameMemory(original, explanation),
            "SEIT-REL-05-N1.expired-feeling-refresh-retains-factual-history");
    }

    private static void RefusedReads()
    {
        // Each invalid witness has its own real seeded state.
        for (int scenario = 0; scenario < 5; scenario++)
        {
            Fixture f = new Fixture();
            EventId source = f.Argument();
            if (scenario == 4)
            {
                // The real writer API accepts a dangling memory; never explain it as fact.
                f.World.AddMemory(Core, f.B.Id, new MemoryEntry((EventId)99999L,
                    "argument", f.World.GameSeconds, f.B.Home,
                    new List<ResidentId> { f.B.Id, f.A.Id }.AsReadOnly(), "argued", "participant"));
            }
            string before = Authority(f);
            string reason;
            MemoryEntry explanation;
            bool accepted;
            bool empty;
            if (scenario == 0)
            {
                ResidentPanelSnapshot snapshot;
                accepted = f.Panel.TryInspect((ResidentId)99999L, out snapshot, out reason);
                empty = snapshot == null;
            }
            else
            {
                ResidentId resident = scenario == 1 ? (ResidentId)99999L : scenario == 2 ? f.C.Id : f.B.Id;
                EventId requested = scenario >= 3 ? (EventId)99999L : source;
                accepted = f.Panel.TryExplain(resident, requested, out explanation, out reason);
                empty = explanation == null;
            }
            Check(!accepted && empty && !string.IsNullOrWhiteSpace(reason) && Authority(f) == before,
                "SEIT-REL-05-N1.invalid-read-refused-with-reason-unchanged-" + scenario);
        }
        Fixture invalid = new Fixture();
        string unchanged = Authority(invalid);
        CommandResult result = invalid.Panel.Cancel((ResidentId)99999L, (ActionInstanceId)99999L);
        Check(!result.Accepted && result.Reason == RejectReason.InvalidParameters
            && Authority(invalid) == unchanged,
            "SEIT-UI-02-P2.unknown-resident-cancel-refused-unchanged");
    }

    private static void IdentityAndReload()
    {
        Fixture f = new Fixture();
        ResidentPanelSnapshot a = Inspect(f.Panel, f.A.Id);
        ResidentPanelSnapshot b = Inspect(f.Panel, f.B.Id);
        Check(a.Resident.DisplayName == b.Resident.DisplayName && a.Resident.Id != b.Resident.Id
            && a.Resident.Home != b.Resident.Home && a.Resident.Id == f.A.Id && b.Resident.Id == f.B.Id,
            "SEIT-IF-RESOLVE-P2.duplicate-names-keep-identity-and-location");
        EventId source = f.Argument();
        f.World.SetAlive(Core, f.B.Id, false);
        ResidentRecord replacement = f.World.AllocateResident(Core, "Sam", LifeStage.Adult,
            f.B.Household, f.B.Home);
        MemoryEntry memory;
        string reason;
        Check(replacement.Id != f.B.Id && !Inspect(f.Panel, f.B.Id).Resident.IsAlive
            && Inspect(f.Panel, f.B.Id).Resident.Id == f.B.Id
            && f.Panel.TryExplain(f.B.Id, source, out memory, out reason)
            && memory != null && memory.Event == source && memory.Participants[0] == f.B.Id,
            "SEIT-IF-RESOLVE-N10.SEIT-UI-04-N3.dead-history-never-cross-binds-to-same-name");
        WorldState restored = WorldState.LoadFromSnapshot(f.World.ExportSnapshot());
        ResidentPanel rebound = new ResidentPanel(restored, new CommandArbitrator(restored, new Clock(restored)));
        Check(rebound.TryExplain(f.B.Id, source, out memory, out reason)
            && SameMemory(memory, f.Relations.Explain(f.B.Id, source))
            && Inspect(rebound, f.B.Id).Resident.Id == f.B.Id
            && !Inspect(rebound, f.B.Id).Resident.IsAlive,
            "SEIT-UI-04-N2.SEIT-AC-06.durable-event-links-after-local-snapshot-rebind");
    }

    private static bool PublicReadOnly(Type type)
    {
        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            if (!field.IsInitOnly && !field.IsLiteral) return false;
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            if (property.GetSetMethod() != null) return false;
        return true;
    }

    private static bool NoStaticMutable(Type type)
    {
        foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            if (!field.IsInitOnly && !field.IsLiteral) return false;
        foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            if (!NoStaticMutable(nested)) return false;
        return true;
    }

    private static void ReadOnlyAndDeterminism()
    {
        Check(PublicReadOnly(typeof(ResidentPanelSnapshot)),
            "SEIT-UI-02-P1.snapshot-has-no-public-writable-members");
        Check(NoStaticMutable(typeof(ResidentPanel)) && NoStaticMutable(typeof(ResidentPanelSnapshot)),
            "SEIT-UI-02-N1.fast-enter-play-no-static-mutable-product-state");
        for (int run = 0; run < 3; run++)
        {
            Fixture left = new Fixture(1234L);
            Fixture right = new Fixture(1234L);
            EventId le = left.Argument();
            EventId re = right.Argument();
            bool same = le == re;
            for (int tick = 0; tick < 5; tick++)
            {
                left.World.SetGameSeconds(Core, left.World.GameSeconds + 60L);
                right.World.SetGameSeconds(Core, right.World.GameSeconds + 60L);
                left.Relations.Complete(Core, new ActivityId("ACT-REL-04"), left.A.Id, left.B.Id);
                right.Relations.Complete(Core, new ActivityId("ACT-REL-04"), right.A.Id, right.B.Id);
                same &= left.Direct("ACT-EDU-02", QueueMode.Immediate).Accepted
                    && right.Direct("ACT-EDU-02", QueueMode.Immediate).Accepted;
                string before = Authority(left);
                ResidentPanelSnapshot x = Inspect(left.Panel, left.A.Id);
                ResidentPanelSnapshot y = Inspect(right.Panel, right.A.Id);
                MemoryEntry xm;
                MemoryEntry ym;
                string reason;
                same &= left.Panel.TryExplain(left.B.Id, le, out xm, out reason)
                    & right.Panel.TryExplain(right.B.Id, re, out ym, out reason);
                same &= SameMemory(xm, ym) && QueueMatches(left, x) && QueueMatches(right, y)
                    && x.Resident.Id == y.Resident.Id && x.Resident.Memories.Count == y.Resident.Memories.Count
                    && Authority(left) == before && left.World.ComputeStateHash() == right.World.ComputeStateHash()
                    && left.World.RngState == right.World.RngState;
                for (int i = 0; i < 8; i++)
                    same &= x.Resident.GetNeed((Need)i) == y.Resident.GetNeed((Need)i);
                for (int i = 0; i < 6; i++)
                    same &= x.Resident.GetTrait((TraitAxis)i) == y.Resident.GetTrait((TraitAxis)i);
            }
            Check(same, "SEIT-REL-05-N1.SEIT-UI-02-N1.seed-1234-repeated-entry-" + run);
        }
    }

#endif

    public static int Main()
    {
        failures = 0;
        try
        {
#if UI_INSPECT_PRODUCER_CHECK
            // Run only the actual upstream producers before the new API exists.
            Fixture producer = new Fixture();
            EventId source = producer.Argument();
            MemoryEntry memory = producer.Relations.Explain(producer.B.Id, source);
            producer.World.SetGameSeconds(Core, producer.World.GameSeconds + 60L);
            RelationshipResult chat = producer.Relations.Complete(Core,
                new ActivityId("ACT-REL-01"), producer.B.Id, producer.C.Id);
            MemoryEntry learned = producer.Relations.Explain(producer.C.Id, source);
            Check(memory != null && memory.Participants.Count == 2
                && memory.Participants[0] == producer.B.Id && memory.Participants[1] == producer.A.Id
                && producer.B.Feelings.Count == 1 && chat.Applied && learned != null
                && learned.LearnedBy == "told-by:" + ((long)chat.Event).ToString(),
                "SEIT-REL-05-N1.real-producer-argument-feeling-chat-provenance");
            producer.World.AddMemory(Core, producer.B.Id, new MemoryEntry((EventId)99999L,
                "argument", producer.World.GameSeconds, producer.B.Home,
                new List<ResidentId> { producer.B.Id, producer.A.Id }.AsReadOnly(), "argued", "participant"));
            Check(producer.Relations.Explain(producer.B.Id, (EventId)99999L) != null,
                "SEIT-REL-05-N1.real-producer-dangling-memory-negative-input");
            Check(producer.Direct("ACT-FOOD-01", QueueMode.Immediate).Accepted
                && producer.Direct("ACT-EDU-02", QueueMode.Append, CommandSource.PlayerHuman).Accepted
                && producer.Direct("ACT-EDU-02", QueueMode.Append, CommandSource.PlayerAutomated).Accepted
                && producer.Direct("ACT-EDU-02", QueueMode.Append, CommandSource.VoiceMapped).Accepted
                && producer.Commands.EnqueueAutonomous(producer.A.Id, new ActivityId("ACT-SPORT-01"), null),
                "SEIT-UI-02-P1.real-producer-current-mixed-owner-and-source-queue");
#else
            ProjectionAndRefresh();
            OwnershipAndCancellation();
            FactualExplanations();
            RefusedReads();
            IdentityAndReload();
            ReadOnlyAndDeterminism();
#endif
        }
        catch (Exception error)
        {
            Check(false, "SEIT-UI-02-P1.unexpected-exception-" + error);
        }
#if UI_INSPECT_PRODUCER_CHECK
        Console.WriteLine(failures == 0 ? "UI-INSPECT PRODUCER CHECK PASS" : "UI-INSPECT PRODUCER CHECK FAILED");
#else
        Console.WriteLine(failures == 0 ? "UI-INSPECT ALL PASS" : "UI-INSPECT FAILURES: " + failures);
#endif
        return failures == 0 ? 0 : 1;
    }
}
