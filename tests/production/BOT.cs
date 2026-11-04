/* BOT@1 TDD contract. Product file: Assets/Blockparty/Automation/RulePlayer.cs.
 * Namespace Blockparty.Automation; plain C# 9, no Unity dependency.
 *
 * public sealed class PlayerObservation
 *   PlayerObservation(ResidentId resident, FreshnessStamp version, long gameSeconds,
 *     IReadOnlyList<float> needs, IReadOnlyList<AvailableAction> available)
 *   readonly ResidentId Resident; readonly FreshnessStamp Version;
 *   readonly long GameSeconds; readonly IReadOnlyList<float> Needs;
 *   readonly IReadOnlyList<AvailableAction> Available;
 *   Needs uses Need enum order. Only player-visible data; no ResidentRecord,
 *   WorldState, traits, diagnostic context or future-event schedule in this DTO.
 * public sealed class RuleDecision
 *   RuleDecision(CommandEnvelope command, FreshnessStamp version)
 *   readonly CommandEnvelope Command; readonly FreshnessStamp Version;
 * public sealed class RulePlayer
 *   RulePlayer(CommandArbitrator arbitrator)
 *   RuleDecision Decide(PlayerObservation observation) -- pure state read;
 *     chooses an offered activity with its target, returns PlayerAutomated
 *     DirectActivity, next resident sequence, current epoch/issue game seconds.
 *     No choices => null. No particular scoring weights or tie-break prescribed.
 *   CommandResult Apply(RuleDecision decision) -- revalidate full stamp and
 *     automation enabled before CommandArbitrator.Submit; old epoch => StaleEpoch,
 *     other stale versions/disabled => StaleGeneration; rejected input unchanged.
 *     Malformed kind/parameter shapes return InvalidParameters without throwing.
 *   void Tick(IReadOnlyList<PlayerObservation> observations) -- drive idle
 *     residents through Decide/Apply only while enabled and unpaused. No command
 *     issuance from the next takeover tick until explicit arbitrator.EnableAutomation.
 *     Pending player work/current actions are respected; no direct WorldState writes.
 *
 * Existing API: Blockparty.Contracts command/freshness/slot/ledger DTOs;
 * Blockparty.Simulation WorldState, Clock, CommandArbitrator, ActivityExecutor.
 * Fixture setup alone writes via StateWriter.SimCore; the bot receives no writer.
 * Fresh seeded fixtures use seed 731, world 7, epoch 0. No services, engine,
 * clocks, files or shared resources; resource group isolated-csharp.
 * Each SEIT-labelled check is local component evidence, NOT full SEIT acceptance.
 * See red.log for the complete 34-case disposition and pending physical/effect/UI
 * gates. In particular NAV commits ledger markers; FOOD owns catalog need gains.
 */
using System;
using System.Collections.Generic;
using System.Reflection;
using Blockparty.Contracts;
using Blockparty.Simulation;
using Blockparty.Automation;

public sealed class BotChecks
{
    private int failures;
    private long nextCommand = 100000L;

    private sealed class Fixture
    {
        public WorldState World;
        public Clock Clock;
        public CommandArbitrator Commands;
        public ActivityExecutor Executor;
        public RulePlayer Bot;
        public ResidentId Resident;
    }

    private static Fixture NewFixture(LifeStage stage = LifeStage.Adult)
    {
        WorldState world = WorldState.CreateSeeded(7L, 731L, 0, null);
        HomeRecord home = world.AllocateHome(StateWriter.SimCore, 8);
        HouseholdRecord household = world.AllocateHousehold(StateWriter.SimCore, "BOT", home.Id);
        ResidentRecord resident = world.AllocateResident(StateWriter.SimCore, "Bot resident", stage,
            household.Id, home.Id);
        world.SetNeed(StateWriter.SimCore, resident.Id, Need.Hunger, 10.0f);
        Clock clock = new Clock(world);
        CommandArbitrator commands = new CommandArbitrator(world, clock);
        return new Fixture { World = world, Clock = clock, Commands = commands,
            Executor = new ActivityExecutor(world, commands, clock),
            Bot = new RulePlayer(commands), Resident = resident.Id };
    }

    private void Check(bool condition, string name)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
        if (!condition) failures++;
    }

    private static PlayerObservation Observe(Fixture f, string activity = "ACT-FOOD-01", long? target = null)
    {
        ResidentRecord resident;
        f.World.TryGetResident(f.Resident, out resident);
        float[] needs = new float[StateSchema.NeedCount];
        for (int i = 0; i < needs.Length; i++) needs[i] = resident.GetNeed((Need)i);
        return new PlayerObservation(f.Resident, f.Commands.CurrentStamp(f.Resident),
            f.World.GameSeconds, Array.AsReadOnly(needs),
            Array.AsReadOnly(new[] { new AvailableAction(new ActivityId(activity), target) }));
    }

    private CommandEnvelope Direct(Fixture f, string code, QueueMode mode,
        CommandSource source = CommandSource.PlayerAutomated, long? target = null)
    {
        return new CommandEnvelope((CommandId)(nextCommand++), f.World.Epoch, f.World.GameSeconds,
            source, f.Commands.CurrentStamp(f.Resident).Sequence + 1u,
            CommandKind.DirectActivity, f.Resident, new ActivityId(code), target,
            new CommandParameters.Direct(mode));
    }

    private CommandEnvelope Global(Fixture f, CommandKind kind, CommandParameters parameters = null,
        ResidentId? resident = null)
    {
        return new CommandEnvelope((CommandId)(nextCommand++), f.World.Epoch, f.World.GameSeconds,
            CommandSource.PlayerHuman, resident.HasValue
                ? f.Commands.CurrentStamp(resident.Value).Sequence + 1u : f.Commands.WorldSequence + 1u, kind, resident,
            null, null, parameters ?? CommandParameters.None.Instance);
    }

    private static void Step(Fixture f, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            f.Clock.AdvanceRealSeconds(StateWriter.SimCore, 0.1);
            f.Executor.Tick(StateWriter.ActivityExecutor);
        }
    }

    // Includes action identities/order and source as well as the authoritative hash.
    private static string State(Fixture f)
    {
        string state = f.World.ComputeStateHash().ToString() + ":" + f.Commands.Interrupted.Count;
        ActionEntry current = f.Commands.GetCurrent(f.Resident);
        state += "|current=" + Entry(current);
        foreach (ActionEntry pending in f.Commands.GetPending(f.Resident)) state += "|" + Entry(pending);
        return state + "|automation=" + f.Commands.AutomationEnabled + "|speed=" + f.Clock.Speed
            + "|paused=" + f.Clock.Paused + "|worldSequence=" + f.Commands.WorldSequence;
    }

    private static string Entry(ActionEntry entry)
    {
        return entry == null ? "idle" : ((long)entry.Command).ToString() + ":" + (long)entry.Action
            + ":" + entry.Activity.Code + ":" + entry.TargetId + ":" + entry.Owner + ":" + entry.Source;
    }

    private void Reject(Fixture f, RuleDecision decision, RejectReason reason, string name)
    {
        string before = State(f);
        int writes = f.World.WriteAudit.Count;
        CommandResult result = f.Bot.Apply(decision);
        Check(!result.Accepted && result.Reason == reason && State(f) == before
            && f.World.WriteAudit.Count == writes, name);
    }

    private static bool HasStaticMutable(Type type)
    {
        foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            if (!field.IsLiteral && !field.IsInitOnly) return true;
        foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            if (HasStaticMutable(nested)) return true;
        return false;
    }

    private void CommandsAndConsequences()
    {
        Fixture f = NewFixture();
        PlayerObservation observation = Observe(f);
        string before = State(f);
        int writes = f.World.WriteAudit.Count;
        RuleDecision decision = f.Bot.Decide(observation);
        Check(decision != null && State(f) == before && f.World.WriteAudit.Count == writes,
            "SEIT-AUTO-01-N1_decide_has_no_state_writes");
        if (decision == null) return;
        CommandEnvelope command = decision.Command;
        Check(command != null && command.Source == CommandSource.PlayerAutomated
            && command.Kind == CommandKind.DirectActivity && command.Resident == f.Resident
            && command.Epoch == f.World.Epoch && command.IssuedAtGameSeconds == f.World.GameSeconds
            && command.ResidentSequence == observation.Version.Sequence + 1u
            && command.Activity.HasValue && command.Activity.Value.Code == "ACT-FOOD-01"
            && command.TargetId == null && command.Parameters is CommandParameters.Direct
            && decision.Version.Matches(observation.Version),
            "SEIT-AUTO-01-P1_SEIT-AC-23_normal_command_shape");
        CommandResult applied = f.Bot.Apply(decision);
        bool logged = false;
        foreach (CommandLogEntry log in f.Commands.CommandLog)
            if (log.Command == command.Id && log.Accepted && log.Resident == f.Resident) logged = true;
        bool soleWriter = true;
        for (int i = writes; i < f.World.WriteAudit.Count; i++)
            if (f.World.WriteAudit[i].Writer != StateWriter.CommandArbitrator) soleWriter = false;
        Check(applied.Accepted && logged && soleWriter && f.World.EffectLedger.Count == 0,
            "SEIT-AUTO-01-N1_SEIT-IF-CMD-P1_accepted_command_before_effect");
        Step(f, 1);
        ActionEntry running = f.Commands.GetCurrent(f.Resident);
        Check(running != null && running.Command == command.Id && running.Owner == ActionOwner.Player
            && running.Source == CommandSource.PlayerAutomated,
            "SEIT-AUTO-01-P1_player_owner_automated_source");
        Reject(f, decision, RejectReason.StaleGeneration,
            "SEIT-IF-CMD-N5_replayed_decision_is_stale");

        // Real allocated object and executor slot/travel, not an animation stand-in.
        Fixture physical = NewFixture();
        ObjectId stove = physical.World.AllocateObject(StateWriter.SimCore);
        physical.Executor.RegisterObject(stove, new[] { new ApproachPose(2.0f, 0.0f, 0.0f) });
        physical.Bot.Tick(new[] { Observe(physical, "ACT-FOOD-01", (long)stove) });
        Step(physical, 1);
        ActionEntry cooking = physical.Commands.GetCurrent(physical.Resident);
        Check(cooking != null && cooking.Activity.Code == "ACT-FOOD-01",
            "SEIT-AUTO-01-N3_tick_dispatches_real_activity");
        if (cooking == null) return;
        ActionInstanceId action = cooking.Action;
        int issued = physical.Commands.CommandLog.Count;
        for (int i = 0; i < 5; i++)
        {
            physical.Bot.Tick(new[] { Observe(physical, "ACT-FOOD-01", (long)stove) });
            Step(physical, 1);
        }
        Check(physical.Commands.CommandLog.Count == issued && physical.Commands.Interrupted.Count == 0,
            "SEIT-AUTO-01-P1_tick_preserves_running_activity");
        Step(physical, 600); // 60 game minutes covers 2m travel + catalog 45m cooking.
        ExecutionRecord execution = physical.Executor.GetExecution(action);
        bool travel = false;
        bool acquired = false;
        bool released = false;
        if (execution != null)
            foreach (StageRecord stage in execution.Trace)
                if (stage.Stage == ExecutionStage.Travel && !stage.Skipped) travel = true;
        foreach (SlotEvent slot in physical.Executor.ReservationLog)
            if (slot.Hold.Action == action)
            {
                if (slot.Acquired) acquired = true;
                else released = true;
            }
        int commits = 0;
        foreach (EffectLedgerEntry effect in physical.World.EffectLedger)
            if (effect.Action == action && effect.EffectKind == "ACT-FOOD-01"
                && effect.Recipient.First == physical.Resident && effect.State == LedgerState.Committed) commits++;
        Check(execution != null && execution.Outcome == TerminalOutcome.Completed && travel
            && acquired && released && commits == 1 && physical.Executor.GetHold(stove, 0) == null,
            "SEIT-AUTO-01-N3_real_travel_slot_completion_ledger");
        Step(physical, 5);
        Check(physical.World.EffectLedger.Count == 1 && physical.Commands.GetCurrent(physical.Resident) == null,
            "SEIT-AUTO-01-N1_no_duplicate_or_invented_reward_marker");
    }

    private void ObservationAndRepeatEntry()
    {
        Fixture a = NewFixture();
        Fixture b = NewFixture();
        // A hidden personality change is real SIM-CORE input but never observation input.
        b.World.SetTrait(StateWriter.SimCore, b.Resident, TraitAxis.Ambitious, 50);
        RuleDecision left = a.Bot.Decide(Observe(a));
        RuleDecision right = b.Bot.Decide(Observe(b));
        bool visibleOnly = true;
        string[] allowed = { "Resident", "Version", "GameSeconds", "Needs", "Available" };
        foreach (FieldInfo field in typeof(PlayerObservation).GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (Array.IndexOf(allowed, field.Name) < 0 || !field.IsInitOnly) visibleOnly = false;
        foreach (FieldInfo field in typeof(PlayerObservation).GetFields(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            if (field.FieldType == typeof(WorldState) || field.FieldType == typeof(ResidentRecord)
                || field.FieldType == typeof(WorldSnapshot)) visibleOnly = false;
        Check(visibleOnly && left != null && right != null
            && left.Command.Activity == right.Command.Activity && left.Command.TargetId == right.Command.TargetId
            && left.Command.Kind == right.Command.Kind,
            "SEIT-AUTO-01-N2_hidden_trait_excluded_and_decision_unchanged");
        Check(!HasStaticMutable(typeof(RulePlayer)) && !HasStaticMutable(typeof(PlayerObservation))
            && !HasStaticMutable(typeof(RuleDecision)),
            "SEIT-AUTO-01-P1_no_static_mutable_state");
        Fixture empty = NewFixture();
        PlayerObservation noChoices = new PlayerObservation(empty.Resident,
            empty.Commands.CurrentStamp(empty.Resident), empty.World.GameSeconds,
            Observe(empty).Needs, Array.AsReadOnly(new AvailableAction[0]));
        string before = State(empty);
        Check(empty.Bot.Decide(noChoices) == null && State(empty) == before,
            "SEIT-AUTO-01-P1_no_available_choice_issues_nothing");

        Fixture paused = NewFixture();
        paused.Commands.Submit(Global(paused, CommandKind.Pause));
        string frozen = State(paused);
        int issued = paused.Commands.CommandLog.Count;
        paused.Bot.Tick(new[] { Observe(paused) });
        Check(State(paused) == frozen && paused.Commands.CommandLog.Count == issued,
            "SEIT-AUTO-02-P1_paused_tick_issues_nothing");

        bool firstCompleted;
        bool secondCompleted;
        string first = SeededRun(out firstCompleted);
        string second = SeededRun(out secondCompleted);
        Check(firstCompleted && secondCompleted && first == second, "SEIT-AUTO-01-P1_repeated_entry_seed_731_deterministic");
    }

    private static string SeededRun(out bool completed)
    {
        completed = false;
        Fixture f = NewFixture();
        string trace = "seed=731";
        foreach (string code in new[] { "ACT-FOOD-01", "ACT-EDU-02", "ACT-REL-04" })
        {
            RuleDecision decision = f.Bot.Decide(Observe(f, code));
            if (decision == null) return trace + "|missing-" + code;
            CommandResult result = f.Bot.Apply(decision);
            if (!result.Accepted) return trace + "|rejected-" + code;
            trace += "|" + result.Accepted + ":" + (long)decision.Command.Id + ":" + decision.Command.Activity.Value.Code;
            Step(f, 650);
            if (f.Commands.GetCurrent(f.Resident) != null || f.Commands.GetPending(f.Resident).Length != 0)
                return trace + "|unfinished-" + code;
            trace += ":" + State(f);
        }
        completed = true;
        return trace;
    }

    private void Takeover(CommandSource source)
    {
        Fixture f = NewFixture();
        f.Bot.Tick(new[] { Observe(f) });
        Step(f, 1);
        ActionEntry old = f.Commands.GetCurrent(f.Resident);
        Check(old != null, "SEIT-AUTO-02-P1_automation_started_" + source);
        RuleDecision pending = f.Bot.Decide(Observe(f, "ACT-SPORT-01"));
        ActionContextSnapshot offered = f.Commands.Snapshot(f.Resident);
        DecisionProposal late = new DecisionProposal((GatewayRequestId)118L,
            new ActivityId("ACT-SPORT-01"), null, offered.Version);
        uint generation = offered.Version.ControlGeneration;
        WorldEpoch epoch = f.World.Epoch;
        CommandResult manual = f.Commands.Submit(Direct(f, "ACT-EDU-02", QueueMode.Immediate, source));
        Step(f, 1);
        ActionEntry human = f.Commands.GetCurrent(f.Resident);
        ExecutionRecord actual = human == null ? null : f.Executor.GetExecution(human.Action);
        Check(manual.Accepted && human != null && human.Activity.Code == "ACT-EDU-02"
            && human.Owner == ActionOwner.Player && human.Source == source && actual != null
            && f.Commands.CurrentStamp(f.Resident).ControlGeneration > generation
            && !f.Commands.AutomationEnabled && f.World.Epoch == epoch
            && f.Commands.Interrupted.Count == 1,
            "SEIT-AUTO-02-P1_SEIT-SIM-04-P1_SEIT-AC-24_next_tick_takeover_" + source);
        if (pending != null)
            Reject(f, pending, RejectReason.StaleGeneration,
                "SEIT-AUTO-02-N1_SEIT-AUTO-02-N4_SEIT-TB-01-N3_pending_command_" + source);
        else Check(false, "SEIT-AUTO-02-N1_pending_fixture_missing_" + source);
        string state = State(f);
        CommandResult response = f.Commands.ApplyProposal(f.Resident, late, offered.Available);
        Check(!response.Accepted && response.Reason == RejectReason.StaleGeneration && State(f) == state,
            "SEIT-AUTO-02-N2_SEIT-IF-CMD-N8_SEIT-SIM-04-N1_SEIT-AC-02_late_proposal_" + source);
        int logged = f.Commands.CommandLog.Count;
        // 30 game minutes: prove no issued envelopes, including rejected envelopes.
        for (int i = 0; i < 300; i++)
        {
            f.Bot.Tick(new[] { Observe(f, "ACT-SPORT-01") });
            Step(f, 1);
        }
        Check(f.Commands.CommandLog.Count == logged && !f.Commands.AutomationEnabled
            && f.Commands.Interrupted.Count == 1 && f.World.Epoch == epoch,
            "SEIT-IF-CMD-N2_SEIT-SIM-04-N4_no_issuance_until_reenable_" + source);
        foreach (CommandKind kind in new[] { CommandKind.Pause, CommandKind.Resume,
            CommandKind.SetSpeed, CommandKind.SelectResident })
        {
            CommandParameters parameters = kind == CommandKind.SetSpeed
                ? (CommandParameters)new CommandParameters.Speed(SimulationSpeed.Fast) : CommandParameters.None.Instance;
            ResidentId? resident = kind == CommandKind.SelectResident ? (ResidentId?)f.Resident : null;
            Check(f.Commands.Submit(Global(f, kind, parameters, resident)).Accepted && !f.Commands.AutomationEnabled,
                "SEIT-AUTO-02-N5_sticky_after_" + kind + "_" + source);
        }
        f.Commands.EnableAutomation();
        // Re-enabling must not make an old pending decision fresh again.
        if (pending != null) Reject(f, pending, RejectReason.StaleGeneration,
            "SEIT-IF-CMD-N5_old_control_generation_after_reenable_" + source);
        RuleDecision fresh = f.Bot.Decide(Observe(f, "ACT-SPORT-01"));
        Check(fresh != null && f.Bot.Apply(fresh).Accepted && f.World.Epoch == epoch,
            "SEIT-AUTO-02-P1_explicit_reenable_accepts_fresh_" + source);
    }

    private void QueuePolicies()
    {
        Fixture f = NewFixture();
        Check(f.Commands.EnqueueAutonomous(f.Resident, new ActivityId("ACT-HEALTH-02"), null),
            "SEIT-SIM-04-P1_autonomous_fixture");
        Step(f, 1);
        f.Commands.EnqueueAutonomous(f.Resident, new ActivityId("ACT-REL-04"), null);
        f.Commands.EnqueueAutonomous(f.Resident, new ActivityId("ACT-EDU-02"), null);
        RuleDecision immediate = new RuleDecision(Direct(f, "ACT-FOOD-01", QueueMode.Immediate),
            f.Commands.CurrentStamp(f.Resident));
        Check(f.Bot.Apply(immediate).Accepted, "SEIT-IF-CMD-P2_automated_immediate_accepted");
        Step(f, 1);
        Check(f.Commands.GetCurrent(f.Resident).Activity.Code == "ACT-FOOD-01"
            && f.Commands.GetPending(f.Resident).Length == 0 && f.Commands.Interrupted.Count == 1,
            "SEIT-IF-CMD-P2_SEIT-SIM-04-P1_immediate_interrupts_once");

        f = NewFixture();
        f.Commands.Submit(Direct(f, "ACT-EDU-02", QueueMode.Append, CommandSource.PlayerHuman));
        f.Commands.Submit(Direct(f, "ACT-REL-04", QueueMode.Append, CommandSource.PlayerHuman));
        f.Commands.EnqueueAutonomous(f.Resident, new ActivityId("ACT-HEALTH-02"), null);
        f.Commands.EnableAutomation();
        uint slot = f.Commands.CurrentStamp(f.Resident).DecisionSlotGeneration;
        CommandResult append = f.Bot.Apply(new RuleDecision(Direct(f, "ACT-FOOD-01", QueueMode.Append),
            f.Commands.CurrentStamp(f.Resident)));
        ActionEntry[] entries = f.Commands.GetPending(f.Resident);
        Check(append.Accepted && entries.Length == 3 && entries[0].Activity.Code == "ACT-EDU-02"
            && entries[1].Activity.Code == "ACT-REL-04" && entries[2].Activity.Code == "ACT-FOOD-01"
            && entries[2].Source == CommandSource.PlayerAutomated
            && f.Commands.CurrentStamp(f.Resident).DecisionSlotGeneration > slot,
            "SEIT-SIM-04-P2_SEIT-IF-CMD-P2_append_preserves_player_order");
        Fixture full = NewFixture();
        for (int i = 0; i < 8; i++)
            full.Commands.Submit(Direct(full, "ACT-EDU-02", QueueMode.Append));
        Reject(full, new RuleDecision(Direct(full, "ACT-FOOD-01", QueueMode.Append),
            full.Commands.CurrentStamp(full.Resident)), RejectReason.QueueFull,
            "SEIT-SIM-04-N2_SEIT-IF-CMD-N3_SEIT-IF-CMD-N5_full_append_unchanged");
        Check(full.Bot.Apply(new RuleDecision(Direct(full, "ACT-FOOD-01", QueueMode.Immediate),
            full.Commands.CurrentStamp(full.Resident))).Accepted,
            "SEIT-SIM-04-N2_full_queue_immediate_needs_no_ninth_slot");
        Fixture mixed = NewFixture();
        for (int i = 0; i < 6; i++) mixed.Commands.Submit(Direct(mixed, "ACT-EDU-02", QueueMode.Append));
        for (int i = 0; i < 2; i++) mixed.Commands.EnqueueAutonomous(mixed.Resident, new ActivityId("ACT-HEALTH-02"), null);
        Check(mixed.Bot.Apply(new RuleDecision(Direct(mixed, "ACT-FOOD-01", QueueMode.Append),
            mixed.Commands.CurrentStamp(mixed.Resident))).Accepted && mixed.Commands.GetPending(mixed.Resident).Length == 7,
            "SEIT-SIM-04-N2_append_removes_nonplayer_before_capacity_check");
    }

    private void ControlGenerationWithoutSequenceChange()
    {
        Fixture f = NewFixture();
        RuleDecision pending = f.Bot.Decide(Observe(f));
        FreshnessStamp before = f.Commands.CurrentStamp(f.Resident);
        f.Commands.Takeover(f.Resident);
        FreshnessStamp after = f.Commands.CurrentStamp(f.Resident);
        Check(after.Sequence == before.Sequence && after.ControlGeneration > before.ControlGeneration,
            "SEIT-AUTO-02-N1_takeover_control_generation_fixture");
        if (pending == null)
        {
            Check(false, "SEIT-AUTO-02-N1_pending_control_fixture_missing");
            return;
        }
        Reject(f, pending, RejectReason.StaleGeneration,
            "SEIT-TB-01-N3_pending_control_generation_disabled");
        f.Commands.EnableAutomation();
        Reject(f, pending, RejectReason.StaleGeneration,
            "SEIT-IF-CMD-N5_old_control_generation_cannot_revive");
    }

    private void InvalidInputs()
    {
        foreach (string fault in new[] { "unknown", "stage", "target", "parameters", "epoch", "kind", "dead" })
        {
            Fixture f = NewFixture(fault == "stage" ? LifeStage.Teen : LifeStage.Adult);
            if (fault == "dead") f.World.SetAlive(StateWriter.SimCore, f.Resident, false);
            FreshnessStamp stamp = f.Commands.CurrentStamp(f.Resident);
            CommandEnvelope valid = Direct(f, fault == "stage" ? "ACT-WORK-02" : "ACT-FOOD-01", QueueMode.Append);
            CommandEnvelope bad = new CommandEnvelope(valid.Id,
                fault == "epoch" ? (WorldEpoch)((uint)f.World.Epoch + 1u) : valid.Epoch,
                valid.IssuedAtGameSeconds, valid.Source, valid.ResidentSequence,
                fault == "kind" ? (CommandKind)999 : valid.Kind, valid.Resident,
                fault == "unknown" ? new ActivityId("ACT-UNKNOWN") : valid.Activity,
                fault == "target" ? (long?)999999L : valid.TargetId,
                fault == "parameters" ? (CommandParameters)new CommandParameters.Speed(SimulationSpeed.Normal) : valid.Parameters);
            RejectReason reason = fault == "unknown" ? RejectReason.UnknownActivity
                : fault == "stage" ? RejectReason.StageIneligible : fault == "target" ? RejectReason.TargetMissing
                : fault == "epoch" ? RejectReason.StaleEpoch : fault == "dead" ? RejectReason.ResidentDeceased
                : RejectReason.InvalidParameters;
            Reject(f, new RuleDecision(bad, stamp), reason,
                "SEIT-SIM-04-N3_SEIT-IF-CMD-N4_SEIT-IF-CMD-N7_SEIT-TB-01-N4_invalid_" + fault);
        }
        Fixture loaded = NewFixture();
        RuleDecision oldEpoch = loaded.Bot.Decide(Observe(loaded));
        WorldState restored = WorldState.LoadFromSnapshot(loaded.World.ExportSnapshot());
        loaded.World = restored;
        loaded.Clock = new Clock(restored);
        loaded.Commands = new CommandArbitrator(restored, loaded.Clock);
        loaded.Executor = new ActivityExecutor(restored, loaded.Commands, loaded.Clock);
        loaded.Bot = new RulePlayer(loaded.Commands);
        Check(restored.Epoch != oldEpoch.Version.Epoch, "SEIT-IF-CMD-N1_load_changes_epoch_fixture");
        Reject(loaded, oldEpoch, RejectReason.StaleEpoch,
            "SEIT-IF-CMD-N1_SEIT-SIM-04-N5_old_epoch_after_actual_snapshot_load");

        // The seven canonical kind/parameter failures, each with fresh state.
        for (int fault = 0; fault < 7; fault++)
        {
            Fixture f = NewFixture();
            uint seq = f.Commands.CurrentStamp(f.Resident).Sequence + 1u;
            CommandKind[] kinds = { CommandKind.SetSpeed, CommandKind.DirectActivity, CommandKind.Pause,
                CommandKind.SetSpeed, CommandKind.RenameResident, CommandKind.WillEdit, CommandKind.CaregiverAssign };
            CommandParameters[] parameters = { new CommandParameters.Speed(SimulationSpeed.Normal),
                CommandParameters.None.Instance, CommandParameters.None.Instance,
                new CommandParameters.Speed((SimulationSpeed)5), new CommandParameters.Rename("Name"),
                new CommandParameters.Direct(QueueMode.Append), new CommandParameters.Caregiver(f.Resident) };
            CommandEnvelope malformed = new CommandEnvelope((CommandId)(nextCommand++), f.World.Epoch,
                f.World.GameSeconds, CommandSource.PlayerAutomated, seq, kinds[fault],
                fault == 4 ? (ResidentId?)null : f.Resident,
                fault == 0 || fault == 1 || fault == 6 ? (ActivityId?)new ActivityId("ACT-FOOD-01") : null,
                fault == 2 ? (long?)5L : null, parameters[fault]);
            Reject(f, new RuleDecision(malformed, f.Commands.CurrentStamp(f.Resident)),
                RejectReason.InvalidParameters, "SEIT-IF-CMD-N7_kind_parameter_matrix_" + fault);
        }
    }

    public static int Main()
    {
        BotChecks checks = new BotChecks();
        checks.CommandsAndConsequences();
        checks.ObservationAndRepeatEntry();
        checks.Takeover(CommandSource.PlayerHuman);
        checks.Takeover(CommandSource.VoiceMapped);
        checks.ControlGenerationWithoutSequenceChange();
        checks.QueuePolicies();
        checks.InvalidInputs();
        checks.failures += M1Regressions.Run("BOT");
        Console.WriteLine(checks.failures == 0 ? "BOT ALL PASS" : "BOT FAILURES: " + checks.failures);
        return checks.failures == 0 ? 0 : 1;
    }
}
