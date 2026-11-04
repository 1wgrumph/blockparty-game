// COMMAND production checks. Standalone console program: compiled and run with
// the Unity-bundled Roslyn + netcorerun until TOOLCHAIN lands the Unity test
// path. Positive checks prove Immediate/Append arbitration, one action owner,
// interruption-once semantics, player priority, takeover and freshness
// generations. Negative checks prove typed rejections leave current action,
// queue and world state unchanged. Mirrors the CLOCK/STATE pattern.
using System;
using System.Collections.Generic;
using System.Reflection;
using Blockparty.Contracts;
using Blockparty.Simulation;

public static class CommandChecks
{
    private static int failures;
    private static long nextId = 1000;

    private static void Check(bool condition, string name)
    {
        if (condition)
        {
            Console.WriteLine("PASS " + name);
        }
        else
        {
            failures++;
            Console.WriteLine("FAIL " + name);
        }
    }

    private static CommandId NextCommand()
    {
        nextId++;
        return (CommandId)nextId;
    }

    private static WorldState NewWorld()
    {
        return WorldState.CreateSeeded(7L, 42L, 0, null);
    }

    private static ResidentRecord AddResident(WorldState world, string name, LifeStage stage)
    {
        ResidentRecord[] existing = world.ResidentsSnapshot();
        HouseholdId household;
        HomeId home;
        if (existing.Length == 0)
        {
            HomeRecord homeRecord = world.AllocateHome(StateWriter.SimCore, 8);
            HouseholdRecord householdRecord = world.AllocateHousehold(StateWriter.SimCore, "H", homeRecord.Id);
            household = householdRecord.Id;
            home = homeRecord.Id;
        }
        else
        {
            household = existing[0].Household;
            home = existing[0].Home;
        }
        return world.AllocateResident(StateWriter.SimCore, name, stage, household, home);
    }

    private static CommandEnvelope Direct(WorldState world, ResidentRecord resident, string activity,
        QueueMode mode, CommandSource source, long? targetId)
    {
        ResidentRecord current;
        world.TryGetResident(resident.Id, out current);
        return new CommandEnvelope(
            NextCommand(), world.Epoch, world.GameSeconds, source,
            current.ResidentSequence + 1u, CommandKind.DirectActivity,
            resident.Id, new ActivityId(activity), targetId, new CommandParameters.Direct(mode));
    }

    private static string PendingCodes(CommandArbitrator arbiter, ResidentId resident)
    {
        ActionEntry[] pending = arbiter.GetPending(resident);
        string codes = string.Empty;
        for (int i = 0; i < pending.Length; i++)
        {
            if (i > 0)
            {
                codes += ",";
            }
            codes += ((long)pending[i].Command).ToString() + ":" + pending[i].Activity.Code
                + ":" + ((int)pending[i].Owner).ToString();
        }
        return codes;
    }

    private static bool AllOwnedOnce(CommandArbitrator arbiter, ResidentId resident)
    {
        ActionEntry current = arbiter.GetCurrent(resident);
        if (current != null && !OwnerValid(current))
        {
            return false;
        }
        ActionEntry[] pending = arbiter.GetPending(resident);
        for (int i = 0; i < pending.Length; i++)
        {
            if (!OwnerValid(pending[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool OwnerValid(ActionEntry entry)
    {
        if (entry.Owner != ActionOwner.Player && entry.Owner != ActionOwner.Autonomous
            && entry.Owner != ActionOwner.ModelProposed)
        {
            return false;
        }
        if (entry.Owner == ActionOwner.Player)
        {
            return entry.Source.HasValue;
        }
        return !entry.Source.HasValue;
    }

    private static WillRecord ValidWill()
    {
        List<MoneyShare> shares = new List<MoneyShare>();
        shares.Add(new MoneyShare("H1", 60));
        shares.Add(new MoneyShare("H2", 40));
        return new WillRecord(shares.AsReadOnly(), 50,
            new List<ObjectId>().AsReadOnly(), string.Empty, string.Empty,
            new List<HouseholdId>().AsReadOnly());
    }

    public static int Main()
    {
        Check(InterfaceVersion.Value == 1, "interface-v1");

        // Append preserves earlier player order across source annotations.
        WorldState w1 = NewWorld();
        ResidentRecord r1 = AddResident(w1, "R1", LifeStage.Adult);
        CommandArbitrator a1 = new CommandArbitrator(w1, new Clock(w1));
        CommandResult p1 = a1.Submit(Direct(w1, r1, "ACT-EDU-02", QueueMode.Append, CommandSource.PlayerHuman, null));
        a1.EnableAutomation();
        CommandResult p2 = a1.Submit(Direct(w1, r1, "ACT-FOOD-01", QueueMode.Append, CommandSource.PlayerAutomated, null));
        a1.EnableAutomation();
        CommandResult p3 = a1.Submit(Direct(w1, r1, "ACT-REL-01", QueueMode.Append, CommandSource.VoiceMapped, null));
        ActionEntry[] q1 = a1.GetPending(r1.Id);
        Check(p1.Accepted && p2.Accepted && p3.Accepted, "append-accepted");
        Check(q1.Length == 3 && q1[0].Activity.Code == "ACT-EDU-02"
            && q1[1].Activity.Code == "ACT-FOOD-01" && q1[2].Activity.Code == "ACT-REL-01", "append-order-p1-p2-p3");
        Check(q1[0].Owner == ActionOwner.Player && q1[1].Owner == ActionOwner.Player
            && q1[2].Owner == ActionOwner.Player, "append-one-owner-player");
        Check(q1[0].Source == CommandSource.PlayerHuman && q1[1].Source == CommandSource.PlayerAutomated
            && q1[2].Source == CommandSource.VoiceMapped, "append-sources-annotated");
        Check(AllOwnedOnce(a1, r1.Id), "append-owners-valid");

        // Append removes queued non-player work first.
        WorldState w2 = NewWorld();
        ResidentRecord r2 = AddResident(w2, "R2", LifeStage.Adult);
        CommandArbitrator a2 = new CommandArbitrator(w2, new Clock(w2));
        a2.Submit(Direct(w2, r2, "ACT-EDU-02", QueueMode.Append, CommandSource.PlayerHuman, null));
        a2.Submit(Direct(w2, r2, "ACT-FOOD-01", QueueMode.Append, CommandSource.PlayerHuman, null));
        Check(a2.EnqueueAutonomous(r2.Id, new ActivityId("ACT-HEALTH-02"), null), "autonomous-enqueued");
        Check(a2.GetPending(r2.Id).Length == 3, "autonomous-pending-third");
        CommandResult ap = a2.Submit(Direct(w2, r2, "ACT-REL-01", QueueMode.Append, CommandSource.PlayerHuman, null));
        ActionEntry[] q2 = a2.GetPending(r2.Id);
        Check(ap.Accepted && q2.Length == 3 && q2[2].Activity.Code == "ACT-REL-01"
            && q2[0].Owner == ActionOwner.Player && q2[1].Owner == ActionOwner.Player, "append-drops-nonplayer");

        // Immediate interrupts once, clears pending, marks superseded.
        WorldState w3 = NewWorld();
        ResidentRecord r3 = AddResident(w3, "R3", LifeStage.Adult);
        CommandArbitrator a3 = new CommandArbitrator(w3, new Clock(w3));
        CommandResult first = a3.Submit(Direct(w3, r3, "ACT-HEALTH-02", QueueMode.Immediate, CommandSource.PlayerHuman, null));
        a3.EnableAutomation();
        CommandResult queued = a3.Submit(Direct(w3, r3, "ACT-EDU-02", QueueMode.Append, CommandSource.PlayerAutomated, null));
        a3.EnableAutomation();
        ActionInstanceId running = a3.GetCurrent(r3.Id).Action;
        CommandResult imm = a3.Submit(Direct(w3, r3, "ACT-FOOD-01", QueueMode.Immediate, CommandSource.PlayerHuman, null));
        Check(first.Accepted && queued.Accepted && imm.Accepted, "immediate-accepted");
        Check(a3.GetCurrent(r3.Id) != null && a3.GetCurrent(r3.Id).Activity.Code == "ACT-FOOD-01"
            && a3.GetCurrent(r3.Id).Owner == ActionOwner.Player, "immediate-runs-next");
        Check(a3.GetPending(r3.Id).Length == 0, "immediate-clears-pending");
        Check(a3.Interrupted.Count == 1 && a3.Interrupted[0].Action == running
            && a3.Interrupted[0].Owner == ActionOwner.Player, "immediate-interrupts-once");
        int supersededCount = 0;
        for (int i = 0; i < a3.CommandLog.Count; i++)
        {
            if (a3.CommandLog[i].Superseded)
            {
                supersededCount++;
            }
        }
        Check(supersededCount == 1, "immediate-supersedes-replaced");

        // Immediate on a full 8-player queue is accepted: no ninth slot.
        WorldState w4 = NewWorld();
        ResidentRecord r4 = AddResident(w4, "R4", LifeStage.Adult);
        CommandArbitrator a4 = new CommandArbitrator(w4, new Clock(w4));
        for (int i = 0; i < 8; i++)
        {
            a4.Submit(Direct(w4, r4, "ACT-EDU-02", QueueMode.Append, CommandSource.PlayerHuman, null));
        }
        Check(a4.GetPending(r4.Id).Length == 8, "queue-fills-to-8");
        CommandResult immFull = a4.Submit(Direct(w4, r4, "ACT-FOOD-01", QueueMode.Immediate, CommandSource.PlayerHuman, null));
        Check(immFull.Accepted && a4.GetCurrent(r4.Id) != null
            && a4.GetCurrent(r4.Id).Activity.Code == "ACT-FOOD-01", "immediate-needs-no-ninth-slot");

        // Immediate while paused waits for the next unpaused tick.
        WorldState w5 = NewWorld();
        ResidentRecord r5 = AddResident(w5, "R5", LifeStage.Adult);
        Clock c5 = new Clock(w5);
        CommandArbitrator a5 = new CommandArbitrator(w5, c5);
        CommandResult run5 = a5.Submit(Direct(w5, r5, "ACT-HEALTH-02", QueueMode.Immediate, CommandSource.PlayerHuman, null));
        c5.Pause();
        CommandResult immPaused = a5.Submit(Direct(w5, r5, "ACT-FOOD-01", QueueMode.Immediate, CommandSource.PlayerHuman, null));
        Check(run5.Accepted && immPaused.Accepted, "immediate-paused-accepted");
        Check(a5.GetCurrent(r5.Id) == null && a5.GetPending(r5.Id).Length == 1, "immediate-paused-waits");
        Check(!a5.PromoteNext(r5.Id) && a5.GetCurrent(r5.Id) == null, "promote-paused-noop");
        c5.Resume();
        Check(a5.PromoteNext(r5.Id) && a5.GetCurrent(r5.Id).Activity.Code == "ACT-FOOD-01", "promote-resumed-runs");
        Check(a5.Interrupted.Count == 1, "paused-interrupt-once");

        // CancelCurrent interrupts once; the resident accepts a new command.
        WorldState w6 = NewWorld();
        ResidentRecord r6 = AddResident(w6, "R6", LifeStage.Adult);
        CommandArbitrator a6 = new CommandArbitrator(w6, new Clock(w6));
        a6.Submit(Direct(w6, r6, "ACT-FOOD-01", QueueMode.Immediate, CommandSource.PlayerHuman, null));
        ResidentRecord r6now;
        w6.TryGetResident(r6.Id, out r6now);
        CommandEnvelope cancel = new CommandEnvelope(NextCommand(), w6.Epoch, w6.GameSeconds,
            CommandSource.PlayerHuman, r6now.ResidentSequence + 1u, CommandKind.CancelCurrent,
            r6.Id, null, null, CommandParameters.None.Instance);
        CommandResult cancelled = a6.Submit(cancel);
        Check(cancelled.Accepted && a6.GetCurrent(r6.Id) == null && a6.Interrupted.Count == 1, "cancel-current-interrupts");
        CommandResult after = a6.Submit(Direct(w6, r6, "ACT-EDU-02", QueueMode.Immediate, CommandSource.PlayerHuman, null));
        Check(after.Accepted && a6.GetCurrent(r6.Id).Activity.Code == "ACT-EDU-02", "cancel-then-new-command");

        // CancelQueued removes the named entry and keeps the rest in order.
        WorldState w7 = NewWorld();
        ResidentRecord r7 = AddResident(w7, "R7", LifeStage.Adult);
        CommandArbitrator a7 = new CommandArbitrator(w7, new Clock(w7));
        CommandEnvelope qe1 = Direct(w7, r7, "ACT-EDU-02", QueueMode.Append, CommandSource.PlayerHuman, null);
        a7.Submit(qe1);
        CommandEnvelope qe2 = Direct(w7, r7, "ACT-FOOD-01", QueueMode.Append, CommandSource.PlayerHuman, null);
        a7.Submit(qe2);
        CommandEnvelope qe3 = Direct(w7, r7, "ACT-REL-01", QueueMode.Append, CommandSource.PlayerHuman, null);
        a7.Submit(qe3);
        ResidentRecord r7now;
        w7.TryGetResident(r7.Id, out r7now);
        CommandEnvelope cancelQ = new CommandEnvelope(NextCommand(), w7.Epoch, w7.GameSeconds,
            CommandSource.PlayerHuman, r7now.ResidentSequence + 1u, CommandKind.CancelQueued,
            r7.Id, null, (long)qe2.Id, CommandParameters.None.Instance);
        CommandResult cancelQResult = a7.Submit(cancelQ);
        ActionEntry[] q7 = a7.GetPending(r7.Id);
        Check(cancelQResult.Accepted && q7.Length == 2 && q7[0].Command == qe1.Id
            && q7[1].Command == qe3.Id, "cancel-queued-removes-named");

        // Takeover disables automation until explicit re-enable.
        WorldState w8 = NewWorld();
        ResidentRecord r8 = AddResident(w8, "R8", LifeStage.Adult);
        CommandArbitrator a8 = new CommandArbitrator(w8, new Clock(w8));
        uint controlBefore;
        ResidentRecord r8rec;
        w8.TryGetResident(r8.Id, out r8rec);
        controlBefore = r8rec.ControlGeneration;
        CommandResult take = a8.Submit(Direct(w8, r8, "ACT-FOOD-01", QueueMode.Immediate, CommandSource.PlayerHuman, null));
        w8.TryGetResident(r8.Id, out r8rec);
        Check(take.Accepted && !a8.AutomationEnabled
            && r8rec.ControlGeneration == controlBefore + 1u, "takeover-disables-automation");
        CommandResult autoAfter = a8.Submit(Direct(w8, r8, "ACT-EDU-02", QueueMode.Append, CommandSource.PlayerAutomated, null));
        Check(!autoAfter.Accepted && autoAfter.Reason == RejectReason.StaleGeneration, "automated-after-takeover-stale");
        a8.EnableAutomation();
        CommandResult autoAgain = a8.Submit(Direct(w8, r8, "ACT-EDU-02", QueueMode.Append, CommandSource.PlayerAutomated, null));
        Check(a8.AutomationEnabled && autoAgain.Accepted, "reenable-resumes-automation");

        // Explicit takeover without a command preserves the world.
        WorldState w9 = NewWorld();
        ResidentRecord r9 = AddResident(w9, "R9", LifeStage.Adult);
        CommandArbitrator a9 = new CommandArbitrator(w9, new Clock(w9));
        a9.Submit(Direct(w9, r9, "ACT-EDU-02", QueueMode.Append, CommandSource.PlayerHuman, null));
        a9.EnableAutomation();
        string beforeTakeover = PendingCodes(a9, r9.Id);
        int hashBeforeTakeover = w9.ComputeStateHash();
        a9.Takeover(r9.Id);
        CommandResult autoBlocked = a9.Submit(Direct(w9, r9, "ACT-FOOD-01", QueueMode.Append, CommandSource.PlayerAutomated, null));
        Check(!a9.AutomationEnabled && !autoBlocked.Accepted
            && autoBlocked.Reason == RejectReason.StaleGeneration, "explicit-takeover-blocks-auto");
        Check(PendingCodes(a9, r9.Id) == beforeTakeover, "explicit-takeover-preserves-queue");
        ResidentRecord r9rec;
        w9.TryGetResident(r9.Id, out r9rec);
        Check(w9.ComputeStateHash() != hashBeforeTakeover, "takeover-bumps-control-audit");

        // Fresh proposal applies once as ModelProposed; replay is stale.
        WorldState w10 = NewWorld();
        ResidentRecord r10 = AddResident(w10, "R10", LifeStage.Adult);
        CommandArbitrator a10 = new CommandArbitrator(w10, new Clock(w10));
        List<AvailableAction> offered = new List<AvailableAction>();
        offered.Add(new AvailableAction(new ActivityId("ACT-SPORT-01"), null));
        FreshnessStamp stamp10 = a10.CurrentStamp(r10.Id);
        DecisionProposal proposal = new DecisionProposal((GatewayRequestId)1L,
            new ActivityId("ACT-SPORT-01"), null, stamp10);
        CommandResult applied = a10.ApplyProposal(r10.Id, proposal, offered.AsReadOnly());
        ActionEntry[] q10 = a10.GetPending(r10.Id);
        Check(applied.Accepted && q10.Length == 1 && q10[0].Owner == ActionOwner.ModelProposed
            && !q10[0].Source.HasValue, "proposal-applies-model-owned");
        CommandResult replay = a10.ApplyProposal(r10.Id, proposal, offered.AsReadOnly());
        Check(!replay.Accepted && replay.Reason == RejectReason.StaleGeneration
            && a10.GetPending(r10.Id).Length == 1, "proposal-replay-stale");

        // TR-01 analogue: a late proposal cannot undo a newer player command.
        WorldState w11 = NewWorld();
        ResidentRecord r11 = AddResident(w11, "R11", LifeStage.Adult);
        CommandArbitrator a11 = new CommandArbitrator(w11, new Clock(w11));
        FreshnessStamp old = a11.CurrentStamp(r11.Id);
        CommandResult player = a11.Submit(Direct(w11, r11, "ACT-FOOD-01", QueueMode.Immediate, CommandSource.PlayerHuman, null));
        a11.EnableAutomation();
        List<AvailableAction> offered11 = new List<AvailableAction>();
        offered11.Add(new AvailableAction(new ActivityId("ACT-SPORT-01"), null));
        DecisionProposal late = new DecisionProposal((GatewayRequestId)2L,
            new ActivityId("ACT-SPORT-01"), null, old);
        CommandResult lateResult = a11.ApplyProposal(r11.Id, late, offered11.AsReadOnly());
        Check(player.Accepted && !lateResult.Accepted && lateResult.Reason == RejectReason.StaleGeneration, "late-proposal-discarded");
        Check(a11.GetCurrent(r11.Id).Activity.Code == "ACT-FOOD-01"
            && a11.GetCurrent(r11.Id).Owner == ActionOwner.Player, "player-command-survives");

        // Pause, Resume and SetSpeed drive the one clock.
        WorldState w12 = NewWorld();
        ResidentRecord r12 = AddResident(w12, "R12", LifeStage.Adult);
        Clock c12 = new Clock(w12);
        CommandArbitrator a12 = new CommandArbitrator(w12, c12);
        CommandEnvelope pause = new CommandEnvelope(NextCommand(), w12.Epoch, w12.GameSeconds,
            CommandSource.PlayerHuman, a12.WorldSequence + 1u, CommandKind.Pause,
            null, null, null, CommandParameters.None.Instance);
        Check(a12.Submit(pause).Accepted && c12.Paused, "pause-command-parks");
        c12.AdvanceRealSeconds(StateWriter.SimCore, 10.0);
        Check(w12.GameSeconds == 0L, "paused-clock-frozen");
        CommandEnvelope resume = new CommandEnvelope(NextCommand(), w12.Epoch, w12.GameSeconds,
            CommandSource.PlayerHuman, a12.WorldSequence + 1u, CommandKind.Resume,
            null, null, null, CommandParameters.None.Instance);
        Check(a12.Submit(resume).Accepted && !c12.Paused, "resume-command-restores");
        CommandEnvelope speed = new CommandEnvelope(NextCommand(), w12.Epoch, w12.GameSeconds,
            CommandSource.PlayerHuman, a12.WorldSequence + 1u, CommandKind.SetSpeed,
            null, null, null, new CommandParameters.Speed(SimulationSpeed.Fast));
        Check(a12.Submit(speed).Accepted && c12.Speed == SimulationSpeed.Fast, "setspeed-command-routes");
        ResidentRecord r12rec;
        w12.TryGetResident(r12.Id, out r12rec);
        Check(r12rec.ResidentSequence == 0u, "global-commands-skip-resident-seq");

        // Presentation-only commands accept without touching simulation.
        WorldState w13 = NewWorld();
        ResidentRecord r13 = AddResident(w13, "R13", LifeStage.Adult);
        CommandArbitrator a13 = new CommandArbitrator(w13, new Clock(w13));
        ResidentRecord r13now;
        w13.TryGetResident(r13.Id, out r13now);
        uint seq13 = r13now.ResidentSequence;
        CommandEnvelope select = new CommandEnvelope(NextCommand(), w13.Epoch, w13.GameSeconds,
            CommandSource.PlayerHuman, seq13 + 1u, CommandKind.SelectResident,
            r13.Id, null, null, CommandParameters.None.Instance);
        CommandEnvelope follow = new CommandEnvelope(NextCommand(), w13.Epoch, w13.GameSeconds,
            CommandSource.PlayerHuman, seq13 + 2u, CommandKind.FollowResident,
            r13.Id, null, null, CommandParameters.None.Instance);
        CommandEnvelope unfollow = new CommandEnvelope(NextCommand(), w13.Epoch, w13.GameSeconds,
            CommandSource.PlayerHuman, a13.WorldSequence + 1u, CommandKind.StopFollowing,
            null, null, null, CommandParameters.None.Instance);
        Check(a13.Submit(select).Accepted && a13.Submit(follow).Accepted
            && a13.Submit(unfollow).Accepted, "presentation-accepted");
        Check(a13.GetCurrent(r13.Id) == null && a13.GetPending(r13.Id).Length == 0
            && a13.AutomationEnabled, "presentation-no-queue-no-takeover");

        // Rename applies through the audited writer; blank takes a default.
        WorldState w14 = NewWorld();
        ResidentRecord r14 = AddResident(w14, "R14", LifeStage.Adult);
        CommandArbitrator a14 = new CommandArbitrator(w14, new Clock(w14));
        ResidentRecord r14now;
        w14.TryGetResident(r14.Id, out r14now);
        CommandEnvelope rename = new CommandEnvelope(NextCommand(), w14.Epoch, w14.GameSeconds,
            CommandSource.PlayerHuman, r14now.ResidentSequence + 1u, CommandKind.RenameResident,
            r14.Id, null, null, new CommandParameters.Rename("Zed"));
        Check(a14.Submit(rename).Accepted, "rename-accepted");
        w14.TryGetResident(r14.Id, out r14now);
        Check(r14now.DisplayName == "Zed", "rename-applies");

        // Valid will stores; an invalid will keeps the last valid one.
        WorldState w15 = NewWorld();
        ResidentRecord r15 = AddResident(w15, "R15", LifeStage.Adult);
        CommandArbitrator a15 = new CommandArbitrator(w15, new Clock(w15));
        ResidentRecord r15now;
        w15.TryGetResident(r15.Id, out r15now);
        CommandEnvelope willOk = new CommandEnvelope(NextCommand(), w15.Epoch, w15.GameSeconds,
            CommandSource.PlayerHuman, r15now.ResidentSequence + 1u, CommandKind.WillEdit,
            r15.Id, null, null, new CommandParameters.Will(ValidWill()));
        Check(a15.Submit(willOk).Accepted, "will-valid-stores");
        WillRecord stored;
        Check(a15.TryGetLastWill(r15.Id, out stored) && stored.MoneyTotalPercent() == 100, "will-last-valid-read");
        List<MoneyShare> badShares = new List<MoneyShare>();
        badShares.Add(new MoneyShare("H1", 60));
        WillRecord badWill = new WillRecord(badShares.AsReadOnly(), 50,
            new List<ObjectId>().AsReadOnly(), string.Empty, string.Empty,
            new List<HouseholdId>().AsReadOnly());
        w15.TryGetResident(r15.Id, out r15now);
        CommandEnvelope willBad = new CommandEnvelope(NextCommand(), w15.Epoch, w15.GameSeconds,
            CommandSource.PlayerHuman, r15now.ResidentSequence + 1u, CommandKind.WillEdit,
            r15.Id, null, null, new CommandParameters.Will(badWill));
        CommandResult willBadResult = a15.Submit(willBad);
        WillRecord kept;
        Check(!willBadResult.Accepted && willBadResult.Reason == RejectReason.InvalidParameters
            && a15.TryGetLastWill(r15.Id, out kept) && kept.MoneyTotalPercent() == 100, "will-invalid-keeps-last");

        // Caregiver designation records the guardian.
        WorldState w16 = NewWorld();
        ResidentRecord r16a = AddResident(w16, "Kid", LifeStage.Child);
        ResidentRecord r16b = AddResident(w16, "Guardian", LifeStage.Adult);
        CommandArbitrator a16 = new CommandArbitrator(w16, new Clock(w16));
        ResidentRecord r16now;
        w16.TryGetResident(r16a.Id, out r16now);
        CommandEnvelope care = new CommandEnvelope(NextCommand(), w16.Epoch, w16.GameSeconds,
            CommandSource.PlayerHuman, r16now.ResidentSequence + 1u, CommandKind.CaregiverAssign,
            r16a.Id, null, null, new CommandParameters.Caregiver(r16b.Id));
        Check(a16.Submit(care).Accepted, "caregiver-accepted");
        ResidentId guardian;
        Check(a16.TryGetCaregiver(r16a.Id, out guardian) && guardian == r16b.Id, "caregiver-recorded");

        // Travel accepts with and without a target.
        WorldState w17 = NewWorld();
        ResidentRecord r17 = AddResident(w17, "R17", LifeStage.Adult);
        CommandArbitrator a17 = new CommandArbitrator(w17, new Clock(w17));
        ObjectId desk = w17.AllocateObject(StateWriter.SimCore);
        ResidentRecord r17now;
        w17.TryGetResident(r17.Id, out r17now);
        CommandEnvelope travel = new CommandEnvelope(NextCommand(), w17.Epoch, w17.GameSeconds,
            CommandSource.PlayerHuman, r17now.ResidentSequence + 1u, CommandKind.TravelTo,
            r17.Id, null, (long)desk, new CommandParameters.Travel(QueueMode.Append));
        CommandResult travelResult = a17.Submit(travel);
        Check(travelResult.Accepted && a17.GetPending(r17.Id).Length == 1
            && a17.GetPending(r17.Id)[0].Kind == CommandKind.TravelTo, "travel-accepted");

        // Snapshot lists stage-eligible activities and the player head.
        ActionContextSnapshot snap = a17.Snapshot(r17.Id);
        bool hasWork = false;
        bool hasFood = false;
        for (int i = 0; i < snap.Available.Count; i++)
        {
            if (snap.Available[i].Activity.Code == "ACT-WORK-02")
            {
                hasWork = true;
            }
            if (snap.Available[i].Activity.Code == "ACT-FOOD-01")
            {
                hasFood = true;
            }
        }
        Check(hasWork && hasFood && snap.PlayerQueueHead.HasValue
            && snap.PlayerQueueHead.Value == travel.Id, "snapshot-eligible-plus-head");
        foreach (var targetKind in new[] { TravelTargetKind.Resident, TravelTargetKind.Object })
        {
            var tagged = new CommandEnvelope(NextCommand(), w17.Epoch, w17.GameSeconds,
                CommandSource.PlayerHuman, r17now.ResidentSequence + 1u, CommandKind.TravelTo,
                r17.Id, null, (long)desk, new CommandParameters.Travel(QueueMode.Immediate, targetKind));
            Check(a17.Submit(tagged).Accepted && a17.GetCurrent(r17.Id).TargetKind == targetKind,
                "R13-command-retains-target-kind-" + targetKind);
        }
        var invalidKind = new CommandEnvelope(NextCommand(), w17.Epoch, w17.GameSeconds,
            CommandSource.PlayerHuman, r17now.ResidentSequence + 1u, CommandKind.TravelTo,
            r17.Id, null, (long)desk, new CommandParameters.Travel(QueueMode.Immediate, (TravelTargetKind)99));
        int unchangedKind = w17.ComputeStateHash();
        Check(a17.Submit(invalidKind).Reason == RejectReason.InvalidParameters
            && w17.ComputeStateHash() == unchangedKind, "R13-unknown-target-kind-refused-unchanged");

        WorldState w18 = NewWorld();
        ResidentRecord teen = AddResident(w18, "Teen", LifeStage.Teen);
        CommandArbitrator a18 = new CommandArbitrator(w18, new Clock(w18));
        ActionContextSnapshot teenSnap = a18.Snapshot(teen.Id);
        bool teenWork2 = false;
        bool teenFood = false;
        for (int i = 0; i < teenSnap.Available.Count; i++)
        {
            if (teenSnap.Available[i].Activity.Code == "ACT-WORK-02")
            {
                teenWork2 = true;
            }
            if (teenSnap.Available[i].Activity.Code == "ACT-FOOD-01")
            {
                teenFood = true;
            }
        }
        Check(!teenWork2 && teenFood && !teenSnap.PlayerQueueHead.HasValue, "snapshot-teen-stages");

        // Stage rules: teen barista level cap, school age cap, child cook ban.
        WorldState w19 = NewWorld();
        ResidentRecord teenWorker = AddResident(w19, "TW", LifeStage.Teen);
        CommandArbitrator a19 = new CommandArbitrator(w19, new Clock(w19));
        Check(a19.Submit(Direct(w19, teenWorker, "ACT-WORK-01", QueueMode.Append, CommandSource.PlayerHuman, null)).Accepted, "teen-barista-level0-ok");
        w19.SetCareer(StateWriter.SimCore, teenWorker.Id, "Barista", 2, 0);
        CommandResult teenL2 = a19.Submit(Direct(w19, teenWorker, "ACT-WORK-01", QueueMode.Append, CommandSource.PlayerHuman, null));
        Check(!teenL2.Accepted && teenL2.Reason == RejectReason.StageIneligible, "teen-barista-level2-banned");
        CommandResult teenTech = a19.Submit(Direct(w19, teenWorker, "ACT-WORK-02", QueueMode.Append, CommandSource.PlayerHuman, null));
        Check(!teenTech.Accepted && teenTech.Reason == RejectReason.StageIneligible, "teen-technician-banned");
        ResidentRecord adult = AddResident(w19, "AD", LifeStage.Adult);
        CommandResult adultSchool = a19.Submit(Direct(w19, adult, "ACT-EDU-01", QueueMode.Append, CommandSource.PlayerHuman, null));
        Check(!adultSchool.Accepted && adultSchool.Reason == RejectReason.StageIneligible, "adult-school-banned");
        ResidentRecord child = AddResident(w19, "CH", LifeStage.Child);
        CommandResult childCook = a19.Submit(Direct(w19, child, "ACT-FOOD-01", QueueMode.Append, CommandSource.PlayerHuman, null));
        CommandResult childStudy = a19.Submit(Direct(w19, child, "ACT-EDU-02", QueueMode.Append, CommandSource.PlayerHuman, null));
        Check(!childCook.Accepted && childCook.Reason == RejectReason.StageIneligible
            && childStudy.Accepted, "child-cook-banned-study-ok");

        int childBefore = w19.ComputeStateHash();
        var childFastFood = a19.Submit(Direct(w19, child, "ACT-FOOD-04", QueueMode.Immediate, CommandSource.PlayerHuman, null));
        Check(!childFastFood.Accepted && childFastFood.Reason == RejectReason.StageIneligible
            && childBefore == w19.ComputeStateHash(), "R24-command-child-fast-food-refused-unchanged");

        // Dependent care requires a caregiver Young Adult or older.
        CommandResult adultCare = a19.Submit(Direct(w19, adult, "ACT-HOME-05", QueueMode.Immediate, CommandSource.PlayerHuman, null));
        CommandResult teenCare = a19.Submit(Direct(w19, teenWorker, "ACT-HOME-05", QueueMode.Immediate, CommandSource.PlayerHuman, null));
        Console.WriteLine("B-1 dependent-care: adult accepted=" + adultCare.Accepted + " reason=" + adultCare.Reason
            + "; teen accepted=" + teenCare.Accepted + " reason=" + teenCare.Reason);
        Check(adultCare.Accepted && !teenCare.Accepted && teenCare.Reason == RejectReason.StageIneligible,
            "B-1-dependent-care-adult-accepted-teen-stage-ineligible");

        // An Elder caregiver must not themselves be care-dependent (SDoc dependent care contract).
        foreach (bool careRequired in new[] { false, true })
        {
            WorldState careWorld = NewWorld();
            ResidentRecord elder = AddResident(careWorld, "Elder", LifeStage.Elder);
            careWorld.SetCareRequired(StateWriter.SimCore, elder.Id, careRequired);
            CommandArbitrator careArbiter = new CommandArbitrator(careWorld, new Clock(careWorld));
            CommandResult elderCare = careArbiter.Submit(Direct(careWorld, elder, "ACT-HOME-05",
                QueueMode.Immediate, CommandSource.PlayerHuman, null));
            bool autonomousCare = careArbiter.EnqueueAutonomous(elder.Id, new ActivityId("ACT-HOME-05"), null);
            ActionContextSnapshot elderSnapshot = careArbiter.Snapshot(elder.Id);
            bool offersCare = false;
            for (int i = 0; i < elderSnapshot.Available.Count; i++)
            {
                if (elderSnapshot.Available[i].Activity.Code == "ACT-HOME-05")
                {
                    offersCare = true;
                }
            }
            Console.WriteLine("B-1 elder careRequired=" + careRequired + ": direct accepted=" + elderCare.Accepted
                + " reason=" + elderCare.Reason + "; autonomous accepted=" + autonomousCare
                + "; snapshot offers=" + offersCare);
            Check(elderCare.Accepted == !careRequired
                && (!careRequired || elderCare.Reason == RejectReason.StageIneligible)
                && autonomousCare == !careRequired && offersCare == !careRequired,
                "B-1-elder-care-required-" + careRequired + "-direct-autonomous-snapshot-eligibility");
        }

        // Interruption never refunds: reserved ledger entries stay reserved.
        WorldState w20 = NewWorld();
        ResidentRecord r20 = AddResident(w20, "R20", LifeStage.Adult);
        CommandArbitrator a20 = new CommandArbitrator(w20, new Clock(w20));
        a20.Submit(Direct(w20, r20, "ACT-FOOD-01", QueueMode.Immediate, CommandSource.PlayerHuman, null));
        ActionInstanceId cooking = a20.GetCurrent(r20.Id).Action;
        Check(w20.ReserveEffect(StateWriter.SimCore, cooking, "Meal", new LedgerRecipient(r20.Id)), "effect-reserved");
        a20.Submit(Direct(w20, r20, "ACT-EDU-02", QueueMode.Immediate, CommandSource.PlayerHuman, null));
        Check(a20.Interrupted.Count == 1 && !w20.ReserveEffect(StateWriter.SimCore, cooking, "Meal", new LedgerRecipient(r20.Id))
            && w20.EffectLedger[0].State == LedgerState.Reserved, "interrupt-keeps-reserved");

        // Every submit is logged once in order.
        Check(a20.CommandLog.Count == 2 && a20.CommandLog[0].Accepted && a20.CommandLog[1].Accepted, "log-two-accepts");

        // Autonomous queue respects the 8-pending bound.
        WorldState w21 = NewWorld();
        ResidentRecord r21 = AddResident(w21, "R21", LifeStage.Adult);
        CommandArbitrator a21 = new CommandArbitrator(w21, new Clock(w21));
        int autoAccepted = 0;
        for (int i = 0; i < 9; i++)
        {
            if (a21.EnqueueAutonomous(r21.Id, new ActivityId("ACT-HEALTH-02"), null))
            {
                autoAccepted++;
            }
        }
        Check(autoAccepted == 8 && a21.GetPending(r21.Id).Length == 8, "autonomous-bound-8");

        // Two arbitrators share nothing.
        WorldState w22a = NewWorld();
        WorldState w22b = NewWorld();
        ResidentRecord r22a = AddResident(w22a, "A", LifeStage.Adult);
        ResidentRecord r22b = AddResident(w22b, "B", LifeStage.Adult);
        CommandArbitrator a22a = new CommandArbitrator(w22a, new Clock(w22a));
        CommandArbitrator a22b = new CommandArbitrator(w22b, new Clock(w22b));
        a22a.Submit(Direct(w22a, r22a, "ACT-FOOD-01", QueueMode.Immediate, CommandSource.PlayerHuman, null));
        Check(a22a.GetCurrent(r22a.Id) != null && a22b.GetCurrent(r22b.Id) == null
            && a22b.AutomationEnabled && !a22a.AutomationEnabled, "arbitrators-independent");

        // No static mutable state anywhere in the package.
        Check(!HasStaticMutable(typeof(CommandArbitrator)) && !HasStaticMutable(typeof(ActionEntry))
            && !HasStaticMutable(typeof(ActionOwner)), "no-static-mutable");

        // ---- Negative checks: typed rejections change nothing. ----

        // Stale epoch.
        WorldState wn1 = NewWorld();
        ResidentRecord rn1 = AddResident(wn1, "N1", LifeStage.Adult);
        CommandArbitrator an1 = new CommandArbitrator(wn1, new Clock(wn1));
        CommandEnvelope good1 = Direct(wn1, rn1, "ACT-FOOD-01", QueueMode.Append, CommandSource.PlayerHuman, null);
        CommandEnvelope staleEpoch = new CommandEnvelope(good1.Id, (WorldEpoch)99u,
            good1.IssuedAtGameSeconds, good1.Source, good1.ResidentSequence,
            good1.Kind, good1.Resident, good1.Activity, good1.TargetId, good1.Parameters);
        int hashN1 = wn1.ComputeStateHash();
        CommandResult staleResult = an1.Submit(staleEpoch);
        Check(!staleResult.Accepted && staleResult.Reason == RejectReason.StaleEpoch
            && wn1.ComputeStateHash() == hashN1 && an1.GetPending(rn1.Id).Length == 0, "reject-stale-epoch");

        // Old or skipped sequences.
        WorldState wn2 = NewWorld();
        ResidentRecord rn2 = AddResident(wn2, "N2", LifeStage.Adult);
        CommandArbitrator an2 = new CommandArbitrator(wn2, new Clock(wn2));
        an2.Submit(Direct(wn2, rn2, "ACT-EDU-02", QueueMode.Append, CommandSource.PlayerHuman, null));
        ResidentRecord rn2now;
        wn2.TryGetResident(rn2.Id, out rn2now);
        CommandEnvelope oldSeq = new CommandEnvelope(NextCommand(), wn2.Epoch, wn2.GameSeconds,
            CommandSource.PlayerHuman, rn2now.ResidentSequence, CommandKind.DirectActivity,
            rn2.Id, new ActivityId("ACT-FOOD-01"), null, new CommandParameters.Direct(QueueMode.Append));
        CommandEnvelope skipSeq = new CommandEnvelope(NextCommand(), wn2.Epoch, wn2.GameSeconds,
            CommandSource.PlayerHuman, rn2now.ResidentSequence + 5u, CommandKind.DirectActivity,
            rn2.Id, new ActivityId("ACT-FOOD-01"), null, new CommandParameters.Direct(QueueMode.Append));
        int hashN2 = wn2.ComputeStateHash();
        CommandResult oldResult = an2.Submit(oldSeq);
        CommandResult skipResult = an2.Submit(skipSeq);
        Check(!oldResult.Accepted && oldResult.Reason == RejectReason.StaleGeneration
            && !skipResult.Accepted && skipResult.Reason == RejectReason.StaleGeneration
            && wn2.ComputeStateHash() == hashN2 && an2.GetPending(rn2.Id).Length == 1, "reject-bad-sequence");

        // Unknown activity, missing target.
        WorldState wn3 = NewWorld();
        ResidentRecord rn3 = AddResident(wn3, "N3", LifeStage.Adult);
        CommandArbitrator an3 = new CommandArbitrator(wn3, new Clock(wn3));
        CommandResult unknown = an3.Submit(Direct(wn3, rn3, "ACT-NOPE-99", QueueMode.Append, CommandSource.PlayerHuman, null));
        CommandResult badTarget = an3.Submit(Direct(wn3, rn3, "ACT-FOOD-01", QueueMode.Append, CommandSource.PlayerHuman, 999999L));
        CommandResult zeroTarget = an3.Submit(Direct(wn3, rn3, "ACT-FOOD-01", QueueMode.Append, CommandSource.PlayerHuman, 0L));
        Check(!unknown.Accepted && unknown.Reason == RejectReason.UnknownActivity
            && !badTarget.Accepted && badTarget.Reason == RejectReason.TargetMissing
            && !zeroTarget.Accepted && zeroTarget.Reason == RejectReason.TargetMissing
            && an3.GetPending(rn3.Id).Length == 0, "reject-unknown-target");

        // Ninth Append on 8 pending player entries: identical queue.
        WorldState wn4 = NewWorld();
        ResidentRecord rn4 = AddResident(wn4, "N4", LifeStage.Adult);
        CommandArbitrator an4 = new CommandArbitrator(wn4, new Clock(wn4));
        for (int i = 0; i < 8; i++)
        {
            an4.Submit(Direct(wn4, rn4, "ACT-EDU-02", QueueMode.Append, CommandSource.PlayerHuman, null));
        }
        string fullBefore = PendingCodes(an4, rn4.Id);
        int hashN4 = wn4.ComputeStateHash();
        CommandResult ninth = an4.Submit(Direct(wn4, rn4, "ACT-FOOD-01", QueueMode.Append, CommandSource.PlayerHuman, null));
        Check(!ninth.Accepted && ninth.Reason == RejectReason.QueueFull
            && PendingCodes(an4, rn4.Id) == fullBefore && wn4.ComputeStateHash() == hashN4, "reject-queue-full");

        // Deceased resident: action commands rejected, follow accepted.
        WorldState wn5 = NewWorld();
        ResidentRecord rn5 = AddResident(wn5, "N5", LifeStage.Adult);
        CommandArbitrator an5 = new CommandArbitrator(wn5, new Clock(wn5));
        wn5.SetAlive(StateWriter.SimCore, rn5.Id, false);
        CommandResult deadDirect = an5.Submit(Direct(wn5, rn5, "ACT-FOOD-01", QueueMode.Append, CommandSource.PlayerHuman, null));
        ResidentRecord rn5now;
        wn5.TryGetResident(rn5.Id, out rn5now);
        CommandEnvelope deadTravel = new CommandEnvelope(NextCommand(), wn5.Epoch, wn5.GameSeconds,
            CommandSource.PlayerHuman, rn5now.ResidentSequence + 1u, CommandKind.TravelTo,
            rn5.Id, null, null, new CommandParameters.Travel(QueueMode.Append));
        CommandEnvelope deadFollow = new CommandEnvelope(NextCommand(), wn5.Epoch, wn5.GameSeconds,
            CommandSource.PlayerHuman, rn5now.ResidentSequence + 1u, CommandKind.FollowResident,
            rn5.Id, null, null, CommandParameters.None.Instance);
        CommandResult deadTravelResult = an5.Submit(deadTravel);
        CommandResult deadFollowResult = an5.Submit(deadFollow);
        Check(!deadDirect.Accepted && deadDirect.Reason == RejectReason.ResidentDeceased
            && !deadTravelResult.Accepted && deadTravelResult.Reason == RejectReason.ResidentDeceased
            && deadFollowResult.Accepted && an5.GetPending(rn5.Id).Length == 0, "reject-deceased-actions");

        // Kind/parameter mismatches (SEIT-IF-CMD-N7 set) plus extras.
        WorldState wn6 = NewWorld();
        ResidentRecord rn6 = AddResident(wn6, "N6", LifeStage.Adult);
        CommandArbitrator an6 = new CommandArbitrator(wn6, new Clock(wn6));
        ResidentRecord rn6now;
        wn6.TryGetResident(rn6.Id, out rn6now);
        uint s6 = rn6now.ResidentSequence;
        List<CommandEnvelope> mismatches = new List<CommandEnvelope>();
        mismatches.Add(new CommandEnvelope(NextCommand(), wn6.Epoch, wn6.GameSeconds, CommandSource.PlayerHuman,
            s6 + 1u, CommandKind.SetSpeed, null, new ActivityId("ACT-FOOD-01"), null,
            new CommandParameters.Speed(SimulationSpeed.Fast)));
        mismatches.Add(new CommandEnvelope(NextCommand(), wn6.Epoch, wn6.GameSeconds, CommandSource.PlayerHuman,
            s6 + 1u, CommandKind.DirectActivity, rn6.Id, new ActivityId("ACT-FOOD-01"), null,
            CommandParameters.None.Instance));
        mismatches.Add(new CommandEnvelope(NextCommand(), wn6.Epoch, wn6.GameSeconds, CommandSource.PlayerHuman,
            an6.WorldSequence + 1u, CommandKind.Pause, rn6.Id, null, 5L, CommandParameters.None.Instance));
        mismatches.Add(new CommandEnvelope(NextCommand(), wn6.Epoch, wn6.GameSeconds, CommandSource.PlayerHuman,
            an6.WorldSequence + 1u, CommandKind.SetSpeed, null, null, null,
            new CommandParameters.Speed((SimulationSpeed)5)));
        mismatches.Add(new CommandEnvelope(NextCommand(), wn6.Epoch, wn6.GameSeconds, CommandSource.PlayerHuman,
            an6.WorldSequence + 1u, CommandKind.RenameResident, null, null, null,
            new CommandParameters.Rename("X")));
        mismatches.Add(new CommandEnvelope(NextCommand(), wn6.Epoch, wn6.GameSeconds, CommandSource.PlayerHuman,
            s6 + 1u, CommandKind.WillEdit, rn6.Id, null, null,
            new CommandParameters.Direct(QueueMode.Append)));
        mismatches.Add(new CommandEnvelope(NextCommand(), wn6.Epoch, wn6.GameSeconds, CommandSource.PlayerHuman,
            s6 + 1u, CommandKind.CaregiverAssign, rn6.Id, new ActivityId("ACT-FOOD-01"), null,
            new CommandParameters.Caregiver(rn6.Id)));
        mismatches.Add(new CommandEnvelope(NextCommand(), wn6.Epoch, wn6.GameSeconds, CommandSource.PlayerHuman,
            s6 + 1u, CommandKind.TravelTo, rn6.Id, new ActivityId("ACT-FOOD-01"), null,
            new CommandParameters.Travel(QueueMode.Append)));
        mismatches.Add(new CommandEnvelope(NextCommand(), wn6.Epoch, wn6.GameSeconds, CommandSource.PlayerHuman,
            s6 + 1u, CommandKind.CancelQueued, rn6.Id, null, null, CommandParameters.None.Instance));
        mismatches.Add(new CommandEnvelope(NextCommand(), wn6.Epoch, wn6.GameSeconds, CommandSource.PlayerHuman,
            s6 + 1u, (CommandKind)999, rn6.Id, null, null, CommandParameters.None.Instance));
        mismatches.Add(new CommandEnvelope(NextCommand(), wn6.Epoch, wn6.GameSeconds, CommandSource.PlayerHuman,
            s6 + 1u, CommandKind.DirectActivity, rn6.Id, new ActivityId("ACT-FOOD-01"), null,
            new CommandParameters.Speed(SimulationSpeed.Normal)));
        int hashN6 = wn6.ComputeStateHash();
        bool allInvalid = true;
        for (int i = 0; i < mismatches.Count; i++)
        {
            CommandResult bad = an6.Submit(mismatches[i]);
            if (bad.Accepted || bad.Reason != RejectReason.InvalidParameters)
            {
                allInvalid = false;
            }
        }
        Check(allInvalid && wn6.ComputeStateHash() == hashN6
            && an6.GetPending(rn6.Id).Length == 0, "reject-kind-param-mismatch");

        // Voice outside the ten supported kinds executes nothing.
        WorldState wn7 = NewWorld();
        ResidentRecord rn7 = AddResident(wn7, "N7", LifeStage.Adult);
        CommandArbitrator an7 = new CommandArbitrator(wn7, new Clock(wn7));
        ResidentRecord rn7now;
        wn7.TryGetResident(rn7.Id, out rn7now);
        uint s7 = rn7now.ResidentSequence;
        ObjectId voiceDestination = wn7.AllocateObject(StateWriter.SimCore);
        CommandEnvelope voiceTravel = new CommandEnvelope(NextCommand(), wn7.Epoch, wn7.GameSeconds,
            CommandSource.VoiceMapped, s7 + 1u, CommandKind.TravelTo,
            rn7.Id, null, (long)voiceDestination, new CommandParameters.Travel(QueueMode.Append, TravelTargetKind.Object));
        CommandEnvelope voiceTargetlessTravel = new CommandEnvelope(NextCommand(), wn7.Epoch, wn7.GameSeconds,
            CommandSource.VoiceMapped, s7 + 1u, CommandKind.TravelTo,
            rn7.Id, null, null, new CommandParameters.Travel(QueueMode.Append));
        CommandEnvelope voiceRename = new CommandEnvelope(NextCommand(), wn7.Epoch, wn7.GameSeconds,
            CommandSource.VoiceMapped, s7 + 1u, CommandKind.RenameResident,
            rn7.Id, null, null, new CommandParameters.Rename("X"));
        CommandResult voiceTargetlessResult = an7.Submit(voiceTargetlessTravel);
        CommandResult voiceTravelResult = an7.Submit(voiceTravel);
        CommandResult voiceRenameResult = an7.Submit(voiceRename);
        CommandEnvelope voiceDirect = Direct(wn7, rn7, "ACT-FOOD-01", QueueMode.Append, CommandSource.VoiceMapped, null);
        CommandResult voiceDirectResult = an7.Submit(voiceDirect);
        // SDoc line 252 keeps TravelTo in the voice set as a named destination.
        Check(voiceTravelResult.Accepted
            && !voiceTargetlessResult.Accepted && voiceTargetlessResult.Reason == RejectReason.InvalidParameters
            && !voiceRenameResult.Accepted && voiceRenameResult.Reason == RejectReason.InvalidParameters
            && voiceDirectResult.Accepted && !an7.AutomationEnabled, "reject-voice-outside-set");

        // Stale-generation proposal after takeover changes nothing.
        WorldState wn8 = NewWorld();
        ResidentRecord rn8 = AddResident(wn8, "N8", LifeStage.Adult);
        CommandArbitrator an8 = new CommandArbitrator(wn8, new Clock(wn8));
        FreshnessStamp pre = an8.CurrentStamp(rn8.Id);
        an8.Takeover(rn8.Id);
        List<AvailableAction> offered8 = new List<AvailableAction>();
        offered8.Add(new AvailableAction(new ActivityId("ACT-SPORT-01"), null));
        DecisionProposal staleProposal = new DecisionProposal((GatewayRequestId)3L,
            new ActivityId("ACT-SPORT-01"), null, pre);
        int hashN8 = wn8.ComputeStateHash();
        CommandResult staleProposalResult = an8.ApplyProposal(rn8.Id, staleProposal, offered8.AsReadOnly());
        Check(!staleProposalResult.Accepted && staleProposalResult.Reason == RejectReason.StaleGeneration
            && wn8.ComputeStateHash() == hashN8 && an8.GetPending(rn8.Id).Length == 0, "reject-stale-proposal");

        // Proposal outside the offered set, or for unknown resident.
        WorldState wn9 = NewWorld();
        ResidentRecord rn9 = AddResident(wn9, "N9", LifeStage.Adult);
        CommandArbitrator an9 = new CommandArbitrator(wn9, new Clock(wn9));
        FreshnessStamp stamp9 = an9.CurrentStamp(rn9.Id);
        List<AvailableAction> offered9 = new List<AvailableAction>();
        offered9.Add(new AvailableAction(new ActivityId("ACT-SPORT-01"), null));
        DecisionProposal offMenu = new DecisionProposal((GatewayRequestId)4L,
            new ActivityId("ACT-FOOD-01"), null, stamp9);
        CommandResult offMenuResult = an9.ApplyProposal(rn9.Id, offMenu, offered9.AsReadOnly());
        CommandResult unknownResident = an9.ApplyProposal((ResidentId)424242L, offMenu, offered9.AsReadOnly());
        Check(!offMenuResult.Accepted && offMenuResult.Reason == RejectReason.InvalidParameters
            && !unknownResident.Accepted && unknownResident.Reason == RejectReason.InvalidParameters
            && an9.GetPending(rn9.Id).Length == 0, "reject-offmenu-proposal");

        // CancelQueued for an unknown command id changes nothing.
        WorldState wn10 = NewWorld();
        ResidentRecord rn10 = AddResident(wn10, "N10", LifeStage.Adult);
        CommandArbitrator an10 = new CommandArbitrator(wn10, new Clock(wn10));
        an10.Submit(Direct(wn10, rn10, "ACT-EDU-02", QueueMode.Append, CommandSource.PlayerHuman, null));
        ResidentRecord rn10now;
        wn10.TryGetResident(rn10.Id, out rn10now);
        CommandEnvelope cancelGhost = new CommandEnvelope(NextCommand(), wn10.Epoch, wn10.GameSeconds,
            CommandSource.PlayerHuman, rn10now.ResidentSequence + 1u, CommandKind.CancelQueued,
            rn10.Id, null, 777777L, CommandParameters.None.Instance);
        string queueBefore = PendingCodes(an10, rn10.Id);
        CommandResult ghostCancelResult = an10.Submit(cancelGhost);
        Check(!ghostCancelResult.Accepted && ghostCancelResult.Reason == RejectReason.InvalidParameters
            && PendingCodes(an10, rn10.Id) == queueBefore, "reject-cancel-ghost");

        // Unknown resident and caregiver-self envelopes.
        WorldState wn11 = NewWorld();
        ResidentRecord rn11 = AddResident(wn11, "N11", LifeStage.Adult);
        CommandArbitrator an11 = new CommandArbitrator(wn11, new Clock(wn11));
        CommandEnvelope ghostResident = new CommandEnvelope(NextCommand(), wn11.Epoch, wn11.GameSeconds,
            CommandSource.PlayerHuman, 1u, CommandKind.DirectActivity,
            (ResidentId)31337L, new ActivityId("ACT-FOOD-01"), null,
            new CommandParameters.Direct(QueueMode.Append));
        ResidentRecord rn11now;
        wn11.TryGetResident(rn11.Id, out rn11now);
        CommandEnvelope selfCare = new CommandEnvelope(NextCommand(), wn11.Epoch, wn11.GameSeconds,
            CommandSource.PlayerHuman, rn11now.ResidentSequence + 1u, CommandKind.CaregiverAssign,
            rn11.Id, null, null, new CommandParameters.Caregiver(rn11.Id));
        CommandResult ghostResult = an11.Submit(ghostResident);
        CommandResult selfCareResult = an11.Submit(selfCare);
        Check(!ghostResult.Accepted && ghostResult.Reason == RejectReason.InvalidParameters
            && !selfCareResult.Accepted && selfCareResult.Reason == RejectReason.InvalidParameters, "reject-ghost-resident");

        // Null inputs throw instead of corrupting state.
        bool nullEnvelope = false;
        bool nullWorld = false;
        try
        {
            an11.Submit(null);
        }
        catch (ArgumentNullException)
        {
            nullEnvelope = true;
        }
        try
        {
            new CommandArbitrator(null, new Clock(wn11));
        }
        catch (ArgumentNullException)
        {
            nullWorld = true;
        }
        Check(nullEnvelope && nullWorld, "null-inputs-throw");

        // CompleteCurrent releases the matching current and starts the pending
        // head exactly once, advancing decision freshness only on mutation.
        WorldState wc = NewWorld();
        ResidentRecord rc = AddResident(wc, "RC", LifeStage.Adult);
        CommandArbitrator ac = new CommandArbitrator(wc, new Clock(wc));
        ac.Submit(Direct(wc, rc, "ACT-HOME-01", QueueMode.Immediate, CommandSource.PlayerHuman, null));
        ac.Submit(Direct(wc, rc, "ACT-REL-04", QueueMode.Append, CommandSource.PlayerHuman, null));
        ActionInstanceId runningId = ac.GetCurrent(rc.Id).Action;
        ResidentRecord rcBefore;
        wc.TryGetResident(rc.Id, out rcBefore);
        uint seqBefore = rcBefore.ResidentSequence;
        uint slotBefore = rcBefore.DecisionSlotGeneration;
        uint controlBeforeC = rcBefore.ControlGeneration;
        ActionInstanceId staleId = wc.AllocateAction(StateWriter.SimCore);
        Check(!ac.CompleteCurrent(rc.Id, staleId)
            && ac.GetCurrent(rc.Id).Action == runningId
            && ac.GetPending(rc.Id).Length == 1
            && ac.CurrentStamp(rc.Id).DecisionSlotGeneration == slotBefore,
            "complete-mismatch-noop");
        Check(ac.CompleteCurrent(rc.Id, runningId)
            && ac.GetCurrent(rc.Id).Activity.Code == "ACT-REL-04"
            && ac.GetPending(rc.Id).Length == 0
            && ac.Interrupted.Count == 0, "complete-releases-starts-head-once");
        ResidentRecord rcAfter;
        wc.TryGetResident(rc.Id, out rcAfter);
        Check(rcAfter.ResidentSequence == seqBefore
            && rcAfter.DecisionSlotGeneration == slotBefore + 1u
            && rcAfter.ControlGeneration == controlBeforeC, "complete-decision-generation-advances");
        Check(!ac.CompleteCurrent(rc.Id, runningId)
            && ac.GetCurrent(rc.Id).Activity.Code == "ACT-REL-04"
            && ac.CurrentStamp(rc.Id).DecisionSlotGeneration == slotBefore + 1u,
            "complete-duplicate-noop");
        ActionInstanceId headId = ac.GetCurrent(rc.Id).Action;
        ac.EnableAutomation();
        ActionContextSnapshot beforeIdle = ac.Snapshot(rc.Id);
        Check(ac.CompleteCurrent(rc.Id, headId) && ac.GetCurrent(rc.Id) == null
            && !ac.CompleteCurrent(rc.Id, headId), "complete-last-goes-idle-once");
        FreshnessStamp afterIdle = ac.CurrentStamp(rc.Id);
        Check(afterIdle.DecisionSlotGeneration == slotBefore + 2u
            && afterIdle.Sequence == seqBefore
            && afterIdle.ControlGeneration == controlBeforeC,
            "complete-idle-decision-generation-advances-once");
        DecisionProposal beforeCompletion = new DecisionProposal((GatewayRequestId)99L,
            new ActivityId("ACT-SPORT-01"), null, beforeIdle.Version);
        CommandResult staleCompletion = ac.ApplyProposal(rc.Id, beforeCompletion, beforeIdle.Available);
        Check(!staleCompletion.Accepted && staleCompletion.Reason == RejectReason.StaleGeneration
            && afterIdle.Matches(ac.CurrentStamp(rc.Id)), "complete-old-proposal-stale");
        ActionContextSnapshot freshCompletion = ac.Snapshot(rc.Id);
        DecisionProposal afterCompletion = new DecisionProposal((GatewayRequestId)100L,
            new ActivityId("ACT-SPORT-01"), null, freshCompletion.Version);
        Check(ac.ApplyProposal(rc.Id, afterCompletion, freshCompletion.Available).Accepted,
            "complete-fresh-proposal-accepted");
        bool completeGhost = false;
        try
        {
            ac.CompleteCurrent((ResidentId)424243L, headId);
        }
        catch (ArgumentException)
        {
            completeGhost = true;
        }
        Check(completeGhost, "complete-unknown-resident-throws");

        if (failures == 0)
        {
            Console.WriteLine("COMMAND ALL PASS");
        }
        else
        {
            Console.WriteLine("COMMAND FAILURES: " + failures.ToString());
        }
        return failures;
    }

    private static bool HasStaticMutable(Type type)
    {
        FieldInfo[] fields = type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        for (int i = 0; i < fields.Length; i++)
        {
            if (!fields[i].IsInitOnly && !fields[i].IsLiteral)
            {
                return true;
            }
        }
        return false;
    }
}
