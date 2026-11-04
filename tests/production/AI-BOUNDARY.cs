// AI-BOUNDARY production checks. Standalone console program: compiled and run
// with the Unity-bundled Roslyn + netcorerun until TOOLCHAIN lands the Unity
// test path. Positive checks prove BP-AI-01 (typed proposals, logged
// rejections), BP-AI-02 (stamped identity, freshness discard), BP-AI-03
// (pause hold + resume revalidation, load cancels), BP-AI-04 (timeout, denial,
// cancellation and malformed output take the local fallback within deadline),
// BP-AI-14 (dev models refused) and BP-SECU-02 (minimized payloads, first-send
// disclosure). Negative checks prove malformed, stale, out-of-set, duplicate,
// rate-limited and unprovisioned responses never mutate state except through
// the defined fallback. No network and no credential material: the
// owner-decided endpoint is a config string that is never contacted.
// Mirrors the PERSONALITY/COMMAND/CLOCK/STATE pattern.
using System;
using System.Collections.Generic;
using System.Reflection;
using Blockparty.AI;
using Blockparty.Contracts;
using Blockparty.Simulation;

public static class AiBoundaryChecks
{
    private const StateWriter Core = StateWriter.SimCore;

    private static int failures;
    private static long nextId = 9000;

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

    private sealed class Stack
    {
        public WorldState World;
        public Clock Clock;
        public CommandArbitrator Arbitrator;
        public Autonomy Autonomy;
        public ProposalBoundary Boundary;
    }

    private static Stack NewStack(long seed)
    {
        WorldState world = WorldState.CreateSeeded(7L, seed, 0, null);
        Clock clock = new Clock(world);
        CommandArbitrator arbitrator = new CommandArbitrator(world, clock);
        Autonomy autonomy = new Autonomy(world, arbitrator);
        return new Stack
        {
            World = world,
            Clock = clock,
            Arbitrator = arbitrator,
            Autonomy = autonomy,
            Boundary = new ProposalBoundary(world, clock, arbitrator, autonomy),
        };
    }

    private static ResidentRecord AddResident(WorldState world, string name)
    {
        ResidentRecord[] existing = world.ResidentsSnapshot();
        HouseholdId household;
        HomeId home;
        if (existing.Length == 0)
        {
            HomeRecord homeRecord = world.AllocateHome(Core, 8);
            HouseholdRecord householdRecord = world.AllocateHousehold(Core, "H", homeRecord.Id);
            household = householdRecord.Id;
            home = homeRecord.Id;
        }
        else
        {
            household = existing[0].Household;
            home = existing[0].Home;
        }
        return world.AllocateResident(Core, name, LifeStage.Adult, household, home);
    }

    private static ServiceEndpoint D1U(bool provisioned)
    {
        return new ServiceEndpoint("meta-d1u", "https://api.meta.ai/v1", "d1-ultra", provisioned);
    }

    private static ServiceEndpoint ImgSvc()
    {
        return new ServiceEndpoint("meta-image", "https://api.meta.ai/v1", "muse-image-1", true);
    }

    private static CommandResult Direct(Stack stack, ResidentRecord resident, string activity, QueueMode mode)
    {
        ResidentRecord current;
        stack.World.TryGetResident(resident.Id, out current);
        nextId++;
        return stack.Arbitrator.Submit(new CommandEnvelope(
            (CommandId)nextId, stack.World.Epoch, stack.World.GameSeconds,
            CommandSource.PlayerHuman, current.ResidentSequence + 1u,
            CommandKind.DirectActivity, resident.Id, new ActivityId(activity),
            null, new CommandParameters.Direct(mode)));
    }

    private static CommandResult PauseCmd(Stack stack)
    {
        nextId++;
        return stack.Arbitrator.Submit(new CommandEnvelope(
            (CommandId)nextId, stack.World.Epoch, stack.World.GameSeconds,
            CommandSource.PlayerHuman, stack.Arbitrator.WorldSequence + 1u,
            CommandKind.Pause, null, null, null, CommandParameters.None.Instance));
    }

    private static ProposalLogEntry LastLog(Stack stack)
    {
        return stack.Boundary.DecisionLog[stack.Boundary.DecisionLog.Count - 1];
    }

    private static bool LogHas(Stack stack, GatewayOutcome outcome, string fragment)
    {
        IReadOnlyList<ProposalLogEntry> log = stack.Boundary.DecisionLog;
        for (int i = 0; i < log.Count; i++)
        {
            if (log[i].Outcome == outcome
                && log[i].Detail.IndexOf(fragment, StringComparison.Ordinal) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    private static int CountOwner(Stack stack, ResidentId resident, ActionOwner owner)
    {
        int count = 0;
        ActionEntry current = stack.Arbitrator.GetCurrent(resident);
        if (current != null && current.Owner == owner)
        {
            count++;
        }
        ActionEntry[] pending = stack.Arbitrator.GetPending(resident);
        for (int i = 0; i < pending.Length; i++)
        {
            if (pending[i].Owner == owner)
            {
                count++;
            }
        }
        return count;
    }

    private static string FirstOffered(Stack stack, ResidentId resident)
    {
        return stack.Arbitrator.Snapshot(resident).Available[0].Activity.Code;
    }

    public static int Main()
    {
        // Stamping: every decision request carries epoch, resident, context
        // version, model id, issue game time and purpose (BP-AI-02).
        {
            Stack stack = NewStack(11L);
            ResidentRecord resident = AddResident(stack.World, "Ada");
            GatewayRequest request = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            Check(request != null, "Stamp.Issued");
            Check(request.Epoch == stack.World.Epoch, "Stamp.Epoch");
            Check(request.Resident.HasValue && request.Resident.Value == resident.Id, "Stamp.Resident");
            Check(request.ContextVersion.Matches(stack.Arbitrator.CurrentStamp(resident.Id)), "Stamp.ContextVersion");
            Check(string.Equals(request.ModelId, "d1-ultra", StringComparison.Ordinal), "Stamp.ModelId");
            Check(request.IssuedAtGameSeconds == stack.World.GameSeconds, "Stamp.IssueTime");
            Check(request.Purpose == GatewayPurpose.ResidentDecision, "Stamp.Purpose");
            Check(stack.Boundary.PendingCount == 1, "Stamp.Pending");
        }

        // Request ids are unique and both requests stay pending.
        {
            Stack stack = NewStack(12L);
            ResidentRecord resident = AddResident(stack.World, "Bo");
            GatewayRequest first = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            GatewayRequest second = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            Check(first != null && second != null && first.Id != second.Id, "Ids.Unique");
            Check(stack.Boundary.PendingCount == 2, "Ids.BothPending");
        }

        // A fresh valid choice applies as a ModelProposed entry (BP-AI-01).
        {
            Stack stack = NewStack(13L);
            ResidentRecord resident = AddResident(stack.World, "Cy");
            GatewayRequest request = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            string code = FirstOffered(stack, resident.Id);
            ProposalVerdict verdict = stack.Boundary.CompleteDecision(request.Id, code, null);
            Check(verdict == ProposalVerdict.Applied, "Apply.Verdict");
            Check(CountOwner(stack, resident.Id, ActionOwner.ModelProposed) == 1, "Apply.Enqueued");
            Check(LastLog(stack).Outcome == GatewayOutcome.Applied, "Apply.Logged");
            Check(LastLog(stack).Detail.IndexOf(code, StringComparison.Ordinal) >= 0, "Apply.DetailNamesChoice");
            Check(LastLog(stack).Sequence == 1L && stack.Boundary.NextSequence == 2L, "Apply.Sequence");
        }

        // Out-of-order completions resolve by freshness, never arrival order:
        // the second completion finds a bumped queue/slot and discards stale.
        {
            Stack stack = NewStack(14L);
            ResidentRecord resident = AddResident(stack.World, "Dee");
            GatewayRequest first = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            GatewayRequest second = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            string code = FirstOffered(stack, resident.Id);
            Check(stack.Boundary.CompleteDecision(second.Id, code, null) == ProposalVerdict.Applied, "Order.SecondApplies");
            Check(stack.Boundary.CompleteDecision(first.Id, code, null) == ProposalVerdict.DiscardedStale, "Order.FirstStale");
            Check(LogHas(stack, GatewayOutcome.Stale, "Stale:"), "Order.StaleLogged");
            Check(LogHas(stack, GatewayOutcome.Stale, "Fallback:"), "Order.FallbackRuns");
            Check(CountOwner(stack, resident.Id, ActionOwner.ModelProposed) == 1, "Order.OneApplied");
        }

        // Duplicate completions are idempotent no-ops.
        {
            Stack stack = NewStack(15L);
            ResidentRecord resident = AddResident(stack.World, "Eli");
            GatewayRequest request = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            string code = FirstOffered(stack, resident.Id);
            stack.Boundary.CompleteDecision(request.Id, code, null);
            int logs = stack.Boundary.DecisionLog.Count;
            Check(stack.Boundary.CompleteDecision(request.Id, code, null) == ProposalVerdict.DuplicateIgnored, "Dup.Verdict");
            Check(stack.Boundary.DecisionLog.Count == logs, "Dup.NoLog");
            Check(CountOwner(stack, resident.Id, ActionOwner.ModelProposed) == 1, "Dup.NoDoubleApply");
            Check(stack.Boundary.CompleteDecision((GatewayRequestId)99999L, code, null) == ProposalVerdict.DuplicateIgnored, "Dup.UnknownId");
        }

        // Pause holds the response; resume applies it (BP-AI-03).
        {
            Stack stack = NewStack(16L);
            ResidentRecord resident = AddResident(stack.World, "Fay");
            GatewayRequest request = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            string code = FirstOffered(stack, resident.Id);
            stack.Clock.Pause();
            Check(stack.Boundary.CompleteDecision(request.Id, code, null) == ProposalVerdict.HeldForPause, "Pause.Held");
            Check(stack.Boundary.PendingCount == 0 && stack.Boundary.HeldCount == 1, "Pause.MovedToHeld");
            Check(CountOwner(stack, resident.Id, ActionOwner.ModelProposed) == 0, "Pause.NothingApplied");
            Check(stack.Boundary.DecisionLog.Count == 0, "Pause.NothingLogged");
            stack.Clock.Resume();
            stack.Boundary.OnResume();
            Check(stack.Boundary.HeldCount == 0, "Pause.Released");
            Check(LogHas(stack, GatewayOutcome.Applied, code), "Pause.AppliedOnResume");
        }

        // A response held during pause is revalidated on resume and discarded
        // when a player command superseded its decision slot (BP-AI-02/03).
        {
            Stack stack = NewStack(17L);
            ResidentRecord resident = AddResident(stack.World, "Gus");
            GatewayRequest request = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            string code = FirstOffered(stack, resident.Id);
            stack.Clock.Pause();
            stack.Boundary.CompleteDecision(request.Id, code, null);
            Check(Direct(stack, resident, "ACT-FOOD-02", QueueMode.Immediate).Accepted, "PauseStale.CommandAccepted");
            stack.Clock.Resume();
            stack.Boundary.OnResume();
            Check(stack.Boundary.HeldCount == 0, "PauseStale.Released");
            Check(LogHas(stack, GatewayOutcome.Stale, "Stale:"), "PauseStale.Discarded");
            Check(CountOwner(stack, resident.Id, ActionOwner.ModelProposed) == 0, "PauseStale.NothingApplied");
        }

        // While paused, deadlines accrue but nothing finalizes (BP-AI-03).
        {
            Stack stack = NewStack(18L);
            ResidentRecord resident = AddResident(stack.World, "Hal");
            stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            stack.Clock.Pause();
            stack.Boundary.AdvanceRealSeconds(30.0);
            Check(stack.Boundary.Tick() == 0, "Frozen.TickNoop");
            Check(stack.Boundary.PendingCount == 1, "Frozen.StillPending");
            Check(stack.Boundary.DecisionLog.Count == 0, "Frozen.NothingLogged");
            stack.Clock.Resume();
            Check(stack.Boundary.Tick() == 1, "Frozen.FinalizedOnResume");
            Check(LogHas(stack, GatewayOutcome.Timeout, "ServiceTimeoutRealSeconds:10"), "Frozen.TimeoutLogged");
        }

        // Cancelling a held response records Cancelled; resume applies nothing.
        {
            Stack stack = NewStack(19L);
            ResidentRecord resident = AddResident(stack.World, "Ivy");
            GatewayRequest request = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            stack.Clock.Pause();
            stack.Boundary.CompleteDecision(request.Id, FirstOffered(stack, resident.Id), null);
            Check(stack.Boundary.Cancel(request.Id), "CancelHeld.True");
            Check(LogHas(stack, GatewayOutcome.Cancelled, "PlayerCancel"), "CancelHeld.Logged");
            stack.Clock.Resume();
            stack.Boundary.OnResume();
            Check(stack.Boundary.HeldCount == 0, "CancelHeld.Empty");
            Check(CountOwner(stack, resident.Id, ActionOwner.ModelProposed) == 0, "CancelHeld.NothingApplied");
        }

        // The 10-real-second service timeout fires exactly at the boundary.
        {
            Stack stack = NewStack(20L);
            ResidentRecord resident = AddResident(stack.World, "Jay");
            stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            stack.Boundary.AdvanceRealSeconds(9.9);
            Check(stack.Boundary.Tick() == 0, "Timeout.Before");
            stack.Boundary.AdvanceRealSeconds(0.1);
            Check(stack.Boundary.Tick() == 1, "Timeout.At");
            Check(LogHas(stack, GatewayOutcome.Timeout, "ServiceTimeoutRealSeconds:10"), "Timeout.Logged");
            Check(LogHas(stack, GatewayOutcome.Timeout, "Fallback:"), "Timeout.Fallback");
        }

        // The 3-game-minute decision deadline fires exactly at 180 game seconds.
        {
            Stack stack = NewStack(21L);
            ResidentRecord resident = AddResident(stack.World, "Kay");
            stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            stack.World.SetGameSeconds(Core, stack.World.GameSeconds + 179L);
            Check(stack.Boundary.Tick() == 0, "Deadline.Before");
            stack.World.SetGameSeconds(Core, stack.World.GameSeconds + 1L);
            Check(stack.Boundary.Tick() == 1, "Deadline.At");
            Check(LogHas(stack, GatewayOutcome.Timeout, "DecisionDeadlineGameMinutes:3"), "Deadline.Logged");
        }

        // Speed changes never extend the real-time deadline, and a fresh
        // response still applies under speed (BP-TIME-04, BP-AI-03).
        {
            Stack stack = NewStack(22L);
            ResidentRecord resident = AddResident(stack.World, "Lou");
            stack.Clock.SetSpeed(SimulationSpeed.Ultra);
            stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            stack.Boundary.AdvanceRealSeconds(10.0);
            Check(stack.Boundary.Tick() == 1, "Speed.TimeoutStillFires");
            Stack fresh = NewStack(23L);
            ResidentRecord other = AddResident(fresh.World, "Mia");
            GatewayRequest request = fresh.Boundary.RequestDecision(other.Id, D1U(true), true, true);
            fresh.Clock.SetSpeed(SimulationSpeed.Fast);
            Check(fresh.Boundary.CompleteDecision(request.Id, FirstOffered(fresh, other.Id), null) == ProposalVerdict.Applied, "Speed.FreshApplies");
        }

        // A timed-out idle resident falls back to a local autonomous decision.
        {
            Stack stack = NewStack(24L);
            ResidentRecord resident = AddResident(stack.World, "Ned");
            stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            stack.Boundary.AdvanceRealSeconds(10.0);
            stack.Boundary.Tick();
            Check(stack.Autonomy.Decisions.Count == 1, "Fallback.DecisionLogged");
            Check(LogHas(stack, GatewayOutcome.Timeout, "Fallback:Enqueued:"), "Fallback.Enqueued");
            Check(CountOwner(stack, resident.Id, ActionOwner.Autonomous) == 1, "Fallback.InQueue");
        }

        // Unprovisioned adapters and missing credentials are never sent: the
        // outcome class is logged and the local path runs (BP-AI-04).
        {
            Stack stack = NewStack(25L);
            ResidentRecord resident = AddResident(stack.World, "Ola");
            Check(stack.Boundary.RequestDecision(resident.Id, D1U(false), true, true) == null, "Refuse.UnprovisionedNull");
            Check(LogHas(stack, GatewayOutcome.Unprovisioned, "Unprovisioned:AdapterNotProvisioned"), "Refuse.UnprovisionedLogged");
            Check(stack.Boundary.RequestDecision(resident.Id, D1U(true), false, true) == null, "Refuse.NoCredentialNull");
            Check(LogHas(stack, GatewayOutcome.Unprovisioned, "Unprovisioned:MissingCredential"), "Refuse.NoCredentialLogged");
            Check(stack.Boundary.PendingCount == 0, "Refuse.NothingPending");
            Check(LogHas(stack, GatewayOutcome.Unprovisioned, "Fallback:"), "Refuse.FallbackRuns");
        }

        // Development models have no runtime role (BP-AI-14).
        {
            Stack stack = NewStack(26L);
            ResidentRecord resident = AddResident(stack.World, "Pam");
            ServiceEndpoint spark = new ServiceEndpoint("x", "https://api.meta.ai/v1", "Muse-Spark-1", true);
            Check(stack.Boundary.RequestDecision(resident.Id, spark, true, true) == null, "Spark.Denied");
            Check(LogHas(stack, GatewayOutcome.Denied, "Denied:ProhibitedRuntimeModel"), "Spark.Logged");
            ServiceEndpoint code = new ServiceEndpoint("x", "https://api.meta.ai/v1", "muse-code", true);
            Check(stack.Boundary.RequestDecision(resident.Id, code, true, true) == null, "MuseCode.Denied");
            Check(stack.Boundary.PendingCount == 0, "Prohibited.NothingPending");
        }

        // First send needs its disclosure; once recorded it persists (BP-SECU-02).
        {
            Stack stack = NewStack(27L);
            ResidentRecord resident = AddResident(stack.World, "Quinn");
            Check(stack.Boundary.RequestDecision(resident.Id, D1U(true), true, false) == null, "Disclose.Blocked");
            Check(LogHas(stack, GatewayOutcome.Denied, "Denied:FirstSendDisclosureMissing"), "Disclose.Logged");
            Check(!stack.Boundary.IsFirstSendDisclosed("meta-d1u"), "Disclose.NotRecorded");
            GatewayRequest request = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            Check(request != null, "Disclose.Issued");
            Check(stack.Boundary.IsFirstSendDisclosed("meta-d1u"), "Disclose.Recorded");
            Check(stack.Boundary.DisclosedWhat("meta-d1u").IndexOf("needs", StringComparison.Ordinal) >= 0, "Disclose.NamesFields");
            stack.Boundary.Cancel(request.Id);
            Check(stack.Boundary.RequestDecision(resident.Id, D1U(true), true, false) != null, "Disclose.Persists");
        }

        // At most 2 concurrent requests per decision purpose; excess takes the
        // local path logged RateLimitedLocal (BP-SEC-07).
        {
            Stack stack = NewStack(28L);
            ResidentRecord resident = AddResident(stack.World, "Rae");
            Check(stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true) != null, "Rate.One");
            Check(stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true) != null, "Rate.Two");
            Check(stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true) == null, "Rate.ThirdRefused");
            Check(LogHas(stack, GatewayOutcome.RateLimitedLocal, "RateLimited:DecisionPurpose"), "Rate.Logged");
            Check(LogHas(stack, GatewayOutcome.RateLimitedLocal, "Fallback:"), "Rate.Fallback");
        }

        // At most 8 concurrent requests game-wide across purposes (BP-SEC-07).
        {
            Stack stack = NewStack(29L);
            for (int i = 0; i < GatewayLimits.MaxConcurrentGameWide; i++)
            {
                GatewayRequest service = stack.Boundary.RequestService(null,
                    GatewayPurpose.ImageGeneration, ImgSvc(), true, true);
                if (service == null)
                {
                    break;
                }
            }
            Check(stack.Boundary.PendingCount == GatewayLimits.MaxConcurrentGameWide, "RateWide.Full");
            ResidentRecord resident = AddResident(stack.World, "Syd");
            Check(stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true) == null, "RateWide.DecisionRefused");
            Check(LogHas(stack, GatewayOutcome.RateLimitedLocal, "RateLimited:GameWide"), "RateWide.Logged");
        }

        // Malformed payloads are inert with a logged rejection reason (BP-AI-01).
        {
            Stack stack = NewStack(30L);
            ResidentRecord resident = AddResident(stack.World, "Tess");
            GatewayRequest empty = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            Check(stack.Boundary.CompleteDecision(empty.Id, string.Empty, null) == ProposalVerdict.DiscardedInvalid, "Malformed.Empty");
            Check(LogHas(stack, GatewayOutcome.Malformed, "Parse:EmptyActivityCode"), "Malformed.EmptyLogged");
            GatewayRequest bad = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            Check(stack.Boundary.CompleteDecision(bad.Id, "ACT FOOD", null) == ProposalVerdict.DiscardedInvalid, "Malformed.Space");
            Check(LogHas(stack, GatewayOutcome.Malformed, "Parse:BadActivityCode"), "Malformed.SpaceLogged");
            Check(CountOwner(stack, resident.Id, ActionOwner.ModelProposed) == 0, "Malformed.Inert");
        }

        // Wellformed but unavailable choices fail simulation validation (BP-AI-01).
        {
            Stack stack = NewStack(31L);
            ResidentRecord resident = AddResident(stack.World, "Ulf");
            GatewayRequest request = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            Check(stack.Boundary.CompleteDecision(request.Id, "ACT-NOPE-99", null) == ProposalVerdict.DiscardedInvalid, "Invalid.OutOfSet");
            Check(LogHas(stack, GatewayOutcome.Malformed, "Validation:InvalidParameters"), "Invalid.Logged");
            Check(CountOwner(stack, resident.Id, ActionOwner.ModelProposed) == 0, "Invalid.Inert");
        }

        // A superseding player command stales the request; the fallback still runs.
        {
            Stack stack = NewStack(32L);
            ResidentRecord resident = AddResident(stack.World, "Vera");
            GatewayRequest request = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            string code = FirstOffered(stack, resident.Id);
            Check(Direct(stack, resident, "ACT-FOOD-02", QueueMode.Immediate).Accepted, "Supersede.CommandAccepted");
            Check(stack.Boundary.CompleteDecision(request.Id, code, null) == ProposalVerdict.DiscardedStale, "Supersede.Stale");
            Check(LogHas(stack, GatewayOutcome.Stale, "Stale:"), "Supersede.Logged");
            Check(CountOwner(stack, resident.Id, ActionOwner.ModelProposed) == 0, "Supersede.Inert");
        }

        // The stale log names the exact mismatched generation (BP-AI-02).
        {
            Stack stack = NewStack(33L);
            ResidentRecord resident = AddResident(stack.World, "Wren");
            GatewayRequest request = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            stack.World.TouchDecisionSlot(Core, resident.Id);
            stack.Boundary.CompleteDecision(request.Id, FirstOffered(stack, resident.Id), null);
            Check(LogHas(stack, GatewayOutcome.Stale, "Stale:DecisionSlot"), "Mismatch.NamesSlot");
        }

        // Manual takeover wins: the pending proposal discards stale and no
        // fallback second-guesses the player (BP-AI-02, BP-SIM-04).
        {
            Stack stack = NewStack(34L);
            ResidentRecord resident = AddResident(stack.World, "Xena");
            GatewayRequest request = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            stack.Arbitrator.Takeover(resident.Id);
            Check(stack.Boundary.CompleteDecision(request.Id, FirstOffered(stack, resident.Id), null) == ProposalVerdict.DiscardedStale, "Takeover.Stale");
            Check(LogHas(stack, GatewayOutcome.Stale, "Stale:Control"), "Takeover.NamesControl");
            Check(LogHas(stack, GatewayOutcome.Stale, "FallbackSkipped:AutomationDisabled"), "Takeover.NoFallback");
            Check(stack.Autonomy.Decisions.Count == 0, "Takeover.NoDecision");
            Check(stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true) == null, "Takeover.RefusesNew");
        }

        // A resident who dies while pending cancels without fallback.
        {
            Stack stack = NewStack(35L);
            ResidentRecord resident = AddResident(stack.World, "Yara");
            stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            stack.World.SetAlive(Core, resident.Id, false);
            Check(stack.Boundary.Tick() == 1, "Death.Finalized");
            Check(LogHas(stack, GatewayOutcome.Cancelled, "ResidentDeceased"), "Death.Logged");
            Check(LogHas(stack, GatewayOutcome.Cancelled, "FallbackSkipped:Deceased"), "Death.NoFallback");
            Check(stack.Autonomy.Decisions.Count == 0, "Death.NoDecision");
            Check(stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true) == null, "Death.RefusesNew");
        }

        // Player cancellation of the initiating feature (BP-SEC-07).
        {
            Stack stack = NewStack(36L);
            ResidentRecord resident = AddResident(stack.World, "Zed");
            GatewayRequest request = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            Check(stack.Boundary.Cancel(request.Id), "Cancel.True");
            Check(LogHas(stack, GatewayOutcome.Cancelled, "PlayerCancel"), "Cancel.Logged");
            Check(!stack.Boundary.Cancel(request.Id), "Cancel.AgainFalse");
            Check(CountOwner(stack, resident.Id, ActionOwner.ModelProposed) == 0, "Cancel.Inert");
        }

        // Residentless purposes stamp the world-command sequence (BP-AI-02).
        {
            Stack stack = NewStack(37L);
            GatewayRequest request = stack.Boundary.RequestService(null,
                GatewayPurpose.ImageGeneration, ImgSvc(), true, true);
            Check(request != null, "Service.Issued");
            Check(!request.Resident.HasValue, "Service.Residentless");
            Check(request.ContextVersion.Sequence == stack.Arbitrator.WorldSequence, "Service.WorldSequence");
            Check(stack.Boundary.CompleteService(request.Id, true, null) == ProposalVerdict.Applied, "Service.Applied");
            Check(LogHas(stack, GatewayOutcome.Applied, "Validated"), "Service.Logged");
        }

        // A world command stales a residentless request; a resident command
        // leaves it fresh.
        {
            Stack stack = NewStack(38L);
            GatewayRequest stale = stack.Boundary.RequestService(null,
                GatewayPurpose.ImageSearch, ImgSvc(), true, true);
            Check(PauseCmd(stack).Accepted, "ServiceStale.PauseAccepted");
            stack.Clock.Resume();
            Check(stack.Boundary.CompleteService(stale.Id, true, null) == ProposalVerdict.DiscardedStale, "ServiceStale.Discarded");
            Check(LogHas(stack, GatewayOutcome.Stale, "Stale:Sequence"), "ServiceStale.NamesSequence");
            Stack other = NewStack(39L);
            ResidentRecord resident = AddResident(other.World, "Ash");
            GatewayRequest kept = other.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, ImgSvc(), true, true);
            Check(Direct(other, resident, "ACT-FOOD-02", QueueMode.Immediate).Accepted, "ServiceFresh.CommandAccepted");
            Check(other.Boundary.CompleteService(kept.Id, true, null) == ProposalVerdict.Applied, "ServiceFresh.Applied");
        }

        // Adapter-owned malformed output logs its reason code (BP-AI-01).
        {
            Stack stack = NewStack(40L);
            GatewayRequest request = stack.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, ImgSvc(), true, true);
            Check(stack.Boundary.CompleteService(request.Id, false, "EmptyTranscript") == ProposalVerdict.DiscardedInvalid, "ServiceMalformed.Verdict");
            Check(LogHas(stack, GatewayOutcome.Malformed, "Parse:EmptyTranscript"), "ServiceMalformed.Logged");
        }

        // Adapter responses held during pause revalidate on resume; the caller
        // takes fresh ones and stale ones vanish.
        {
            Stack stack = NewStack(41L);
            GatewayRequest request = stack.Boundary.RequestService(null,
                GatewayPurpose.ImageGeneration, ImgSvc(), true, true);
            stack.Clock.Pause();
            Check(stack.Boundary.CompleteService(request.Id, true, null) == ProposalVerdict.HeldForPause, "ServicePause.Held");
            stack.Clock.Resume();
            stack.Boundary.OnResume();
            Check(stack.Boundary.HeldCount == 1, "ServicePause.StillHeld");
            GatewayRequest taken = stack.Boundary.RevalidateHeldService(request.Id);
            Check(taken != null && taken.Id == request.Id, "ServicePause.Taken");
            Check(LogHas(stack, GatewayOutcome.Applied, "RevalidatedOnResume"), "ServicePause.Logged");
            Check(stack.Boundary.RevalidateHeldService(request.Id) == null, "ServicePause.TakenOnce");
            Stack stale = NewStack(42L);
            GatewayRequest doomed = stale.Boundary.RequestService(null,
                GatewayPurpose.ImageGeneration, ImgSvc(), true, true);
            stale.Clock.Pause();
            stale.Boundary.CompleteService(doomed.Id, true, null);
            Check(PauseCmd(stale).Accepted, "ServicePauseStale.PauseAccepted");
            stale.Clock.Resume();
            stale.Boundary.OnResume();
            Check(stale.Boundary.HeldCount == 0, "ServicePauseStale.Released");
            Check(stale.Boundary.RevalidateHeldService(doomed.Id) == null, "ServicePauseStale.Gone");
        }

        // Adapter requests share the real-time timeout but not the resident
        // decision deadline.
        {
            Stack stack = NewStack(43L);
            stack.Boundary.RequestService(null, GatewayPurpose.ImageSearch, ImgSvc(), true, true);
            stack.Boundary.AdvanceRealSeconds(10.0);
            Check(stack.Boundary.Tick() == 1, "ServiceTimeout.Fires");
            Check(LogHas(stack, GatewayOutcome.Timeout, "ServiceTimeoutRealSeconds:10"), "ServiceTimeout.Logged");
            Stack slow = NewStack(44L);
            slow.Boundary.RequestService(null, GatewayPurpose.ImageSearch, ImgSvc(), true, true);
            slow.World.SetGameSeconds(Core, slow.World.GameSeconds + 10000L);
            Check(slow.Boundary.Tick() == 0, "ServiceTimeout.NoGameDeadline");
            Check(slow.Boundary.PendingCount == 1, "ServiceTimeout.StillPending");
        }

        // World load cancels every pending and held request with no fallback (BP-AI-03).
        {
            Stack stack = NewStack(45L);
            ResidentRecord resident = AddResident(stack.World, "Blair");
            GatewayRequest decision = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            GatewayRequest service = stack.Boundary.RequestService(null,
                GatewayPurpose.ImageGeneration, ImgSvc(), true, true);
            GatewayRequest third = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            stack.Clock.Pause();
            stack.Boundary.CompleteDecision(third.Id, FirstOffered(stack, resident.Id), null);
            stack.Boundary.NotifyWorldLoaded();
            Check(stack.Boundary.PendingCount == 0 && stack.Boundary.HeldCount == 0, "Load.Cleared");
            Check(stack.Boundary.DecisionLog.Count == 3, "Load.ThreeCancelled");
            Check(LogHas(stack, GatewayOutcome.Cancelled, "WorldLoaded"), "Load.Logged");
            Check(stack.Autonomy.Decisions.Count == 0, "Load.NoFallback");
            Check(stack.Boundary.CompleteDecision(decision.Id, "ACT-FOOD-02", null) == ProposalVerdict.DuplicateIgnored, "Load.DecisionGone");
            Check(stack.Boundary.CompleteService(service.Id, true, null) == ProposalVerdict.DuplicateIgnored, "Load.ServiceGone");
        }

        // The decision payload carries only the minimized fields (BP-SECU-02,
        // BP-AI-05): the resident name exists nowhere on it.
        {
            Stack stack = NewStack(46L);
            ResidentRecord resident = AddResident(stack.World, "ZephyrQuixotec");
            stack.World.SetNeed(Core, resident.Id, Need.Hunger, 12.0f);
            WorldEvent first = stack.World.AppendEvent(Core, "help", resident.Id, "d1");
            WorldEvent second = stack.World.AppendEvent(Core, "gift", resident.Id, "d2");
            List<ResidentId> solo = new List<ResidentId>();
            solo.Add(resident.Id);
            stack.World.AddMemory(Core, resident.Id, new MemoryEntry(first.Event, "help", 0L,
                resident.Home, solo.AsReadOnly(), "ok", string.Empty));
            stack.World.AddMemory(Core, resident.Id, new MemoryEntry(second.Event, "gift", 0L,
                resident.Home, solo.AsReadOnly(), "ok", string.Empty));
            DecisionPayload payload = stack.Boundary.BuildDecisionPayload(resident.Id);
            Check(payload.Needs.Length == StateSchema.NeedCount, "Payload.NeedCount");
            Check(payload.Needs[(int)Need.Hunger] == 12.0f, "Payload.NeedValue");
            ActionContextSnapshot snapshot = stack.Arbitrator.Snapshot(resident.Id);
            Check(payload.AvailableActivityIds.Length == snapshot.Available.Count, "Payload.OfferedCount");
            bool offeredMatch = true;
            for (int i = 0; i < payload.AvailableActivityIds.Length; i++)
            {
                if (!string.Equals(payload.AvailableActivityIds[i], snapshot.Available[i].Activity.Code, StringComparison.Ordinal))
                {
                    offeredMatch = false;
                }
            }
            Check(offeredMatch, "Payload.OfferedMatch");
            Check(payload.KnownHistoryEventIds.Length == 2
                && payload.KnownHistoryEventIds[0] == (long)first.Event
                && payload.KnownHistoryEventIds[1] == (long)second.Event, "Payload.History");
            Check(payload.Constraints.Length > 0, "Payload.Constraints");
            bool leak = false;
            bool nameField = false;
            PropertyInfo[] properties = typeof(DecisionPayload).GetProperties();
            for (int i = 0; i < properties.Length; i++)
            {
                if (properties[i].Name.IndexOf("Name", StringComparison.Ordinal) >= 0)
                {
                    nameField = true;
                }
                object value = properties[i].GetValue(payload, null);
                if (value is string text
                    && text.IndexOf("Zephyr", StringComparison.Ordinal) >= 0)
                {
                    leak = true;
                }
                if (value is string[] codes)
                {
                    for (int j = 0; j < codes.Length; j++)
                    {
                        if (codes[j].IndexOf("Zephyr", StringComparison.Ordinal) >= 0)
                        {
                            leak = true;
                        }
                    }
                }
            }
            Check(!nameField, "Payload.NoNameField");
            Check(!leak, "Payload.NoNameLeak");
        }

        // The decision log is a 1024-record ring with a visible truncation
        // marker and monotonic sequence (BP-SEC-07).
        {
            Stack stack = NewStack(47L);
            ResidentRecord resident = AddResident(stack.World, "Cato");
            ServiceEndpoint down = D1U(false);
            for (int i = 0; i < GatewayLimits.DecisionLogMaxRecords + 1; i++)
            {
                stack.Boundary.RequestDecision(resident.Id, down, true, true);
            }
            Check(stack.Boundary.DecisionLog.Count == GatewayLimits.DecisionLogMaxRecords, "Ring.Bounded");
            Check(stack.Boundary.TruncatedRecords == 1, "Ring.Marker");
            Check(stack.Boundary.DecisionLog[0].Sequence == 2L, "Ring.OldestEvicted");
            Check(stack.Boundary.NextSequence == (long)GatewayLimits.DecisionLogMaxRecords + 2L, "Ring.Monotonic");
            bool ordered = true;
            for (int i = 1; i < stack.Boundary.DecisionLog.Count; i++)
            {
                if (stack.Boundary.DecisionLog[i].Sequence != stack.Boundary.DecisionLog[i - 1].Sequence + 1L)
                {
                    ordered = false;
                }
            }
            Check(ordered, "Ring.Ordered");
        }

        // The log never carries endpoint URLs, key material or bearer tokens.
        {
            Stack stack = NewStack(48L);
            ResidentRecord resident = AddResident(stack.World, "Dara");
            GatewayRequest request = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            stack.Boundary.CompleteDecision(request.Id, FirstOffered(stack, resident.Id), null);
            GatewayRequest bad = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            stack.Boundary.CompleteDecision(bad.Id, string.Empty, null);
            bool clean = true;
            IReadOnlyList<ProposalLogEntry> log = stack.Boundary.DecisionLog;
            for (int i = 0; i < log.Count; i++)
            {
                string both = log[i].ModelId + "|" + log[i].Detail;
                if (both.IndexOf("https://", StringComparison.Ordinal) >= 0
                    || both.IndexOf("Bearer", StringComparison.Ordinal) >= 0
                    || both.IndexOf("MODEL_API_KEY", StringComparison.Ordinal) >= 0
                    || both.IndexOf("sk-", StringComparison.Ordinal) >= 0)
                {
                    clean = false;
                }
            }
            Check(clean, "Secrets.NoneLogged");
        }

        // Argument guards fail closed without logging or mutation.
        {
            Stack stack = NewStack(49L);
            ResidentRecord resident = AddResident(stack.World, "Ember");
            bool threw = false;
            try
            {
                stack.Boundary.RequestDecision((ResidentId)424242L, D1U(true), true, true);
            }
            catch (ArgumentException)
            {
                threw = true;
            }
            Check(threw, "Guards.UnknownResident");
            threw = false;
            try
            {
                stack.Boundary.RequestDecision(resident.Id, null, true, true);
            }
            catch (ArgumentNullException)
            {
                threw = true;
            }
            Check(threw, "Guards.NullService");
            threw = false;
            try
            {
                stack.Boundary.RequestService(resident.Id, GatewayPurpose.ResidentDecision, D1U(true), true, true);
            }
            catch (ArgumentException)
            {
                threw = true;
            }
            Check(threw, "Guards.DecisionPurpose");
            threw = false;
            try
            {
                stack.Boundary.AdvanceRealSeconds(-1.0);
            }
            catch (ArgumentOutOfRangeException)
            {
                threw = true;
            }
            Check(threw, "Guards.NegativeTime");
            threw = false;
            try
            {
                stack.Boundary.BuildDecisionPayload((ResidentId)424242L);
            }
            catch (ArgumentException)
            {
                threw = true;
            }
            Check(threw, "Guards.PayloadUnknownResident");
            Check(stack.Boundary.DecisionLog.Count == 0 && stack.Boundary.PendingCount == 0, "Guards.Clean");
            Check(new ProposalBoundary(stack.World, stack.Clock, stack.Arbitrator, stack.Autonomy) != null, "Guards.Constructs");
        }

        // Review regressions F1-F7: real boundary, clock, state and arbitrator.
        {
            Stack stack = NewStack(701L);
            ResidentRecord resident = AddResident(stack.World, "PausedMalformed");
            GatewayRequest request = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            stack.Clock.Pause();
            stack.Boundary.CompleteDecision(request.Id, string.Empty, null);
            Check(stack.Arbitrator.GetPending(resident.Id).Length == 0, "F1.PauseKeepsQueue");
            stack.Clock.Resume();
            stack.Boundary.OnResume();
            Check(CountOwner(stack, resident.Id, ActionOwner.Autonomous) == 1, "F1.FallbackOnResume");
            int count = stack.Autonomy.Decisions.Count;
            stack.Boundary.OnResume();
            Check(stack.Autonomy.Decisions.Count == count, "F1.FallbackOnce");
        }
        // Both deadlines are enforced at completion, without a preceding Tick.
        for (int kind = 0; kind < 3; kind++)
        {
            Stack stack = NewStack(710L + kind);
            ResidentRecord resident = AddResident(stack.World, "Expired");
            GatewayRequest request = kind == 2
                ? stack.Boundary.RequestService(null, GatewayPurpose.ImageGeneration, ImgSvc(), true, true)
                : stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            if (kind == 1) stack.World.SetGameSeconds(Core, 180L);
            else stack.Boundary.AdvanceRealSeconds(10.0);
            ProposalVerdict verdict = kind == 2
                ? stack.Boundary.CompleteService(request.Id, true, null)
                : stack.Boundary.CompleteDecision(request.Id, FirstOffered(stack, resident.Id), null);
            Check(verdict != ProposalVerdict.Applied && (LogHas(stack, GatewayOutcome.Timeout, "Deadline")
                || LogHas(stack, GatewayOutcome.Timeout, "ServiceTimeout")),
                "F2.ExpiredCompletion." + kind.ToString());
        }
        {
            Stack stack = NewStack(714L);
            ResidentRecord resident = AddResident(stack.World, "LatePaused");
            GatewayRequest request = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            stack.Clock.Pause();
            stack.Boundary.AdvanceRealSeconds(11.0);
            stack.Boundary.Tick();
            stack.Boundary.CompleteDecision(request.Id, FirstOffered(stack, resident.Id), null);
            Check(stack.Arbitrator.GetPending(resident.Id).Length == 0, "F2.LatePauseInert");
            stack.Clock.Resume();
            stack.Boundary.OnResume();
            Check(CountOwner(stack, resident.Id, ActionOwner.ModelProposed) == 0
                && LogHas(stack, GatewayOutcome.Timeout, "ServiceTimeout"), "F2.LatePauseExpires");
        }
        // A response received before the timeout is complete, even if presentation waits.
        {
            Stack stack = NewStack(715L);
            ResidentRecord resident = AddResident(stack.World, "TimelyHeld");
            GatewayRequest request = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            stack.Clock.Pause();
            stack.Boundary.AdvanceRealSeconds(9.0);
            stack.Boundary.CompleteDecision(request.Id, FirstOffered(stack, resident.Id), null);
            stack.Boundary.AdvanceRealSeconds(30.0);
            stack.Clock.Resume();
            stack.Boundary.OnResume();
            Check(CountOwner(stack, resident.Id, ActionOwner.ModelProposed) == 1, "F2.TimelyHeldStillApplies");
        }
        for (int kind = 0; kind < 2; kind++)
        {
            Stack stack = NewStack(720L + kind);
            ResidentRecord resident = AddResident(stack.World, "DeceasedService");
            GatewayRequest request = stack.Boundary.RequestService(resident.Id,
                GatewayPurpose.ImageGeneration, ImgSvc(), true, true);
            if (kind == 0)
            {
                stack.World.SetAlive(Core, resident.Id, false);
                Check(stack.Boundary.CompleteService(request.Id, true, null) != ProposalVerdict.Applied,
                    "F3.PendingDeathInert");
            }
            else
            {
                stack.Clock.Pause();
                stack.Boundary.CompleteService(request.Id, true, null);
                stack.Clock.Resume();
                stack.Boundary.OnResume();
                stack.World.SetAlive(Core, resident.Id, false);
                stack.Boundary.Tick();
                Check(stack.Boundary.RevalidateHeldService(request.Id) == null, "F3.HeldDeathInert");
            }
            Check(LogHas(stack, GatewayOutcome.Cancelled, "ResidentDeceased"), "F3.DeathCancelled." + kind.ToString());
        }
        {
            Stack stack = NewStack(730L);
            GatewayRequest request = stack.Boundary.RequestService(null, GatewayPurpose.ImageGeneration, ImgSvc(), true, true);
            stack.Clock.Pause();
            stack.Boundary.CompleteService(request.Id, true, null);
            stack.Clock.Resume();
            stack.Boundary.OnResume();
            stack.Clock.Pause();
            Check(stack.Boundary.RevalidateHeldService(request.Id) == null && stack.Boundary.HeldCount == 1,
                "F4.RepauseKeepsHeld");
            stack.Clock.Resume();
            stack.Boundary.OnResume();
            Check(stack.Boundary.RevalidateHeldService(request.Id) != null, "F4.NextResumeApplies");
        }
        // Reflection makes the missing terminal API an executable red case on the baseline.
        MethodInfo failureApi = typeof(ProposalBoundary).GetMethod("CompleteFailure",
            new Type[] { typeof(GatewayRequestId), typeof(GatewayOutcome) });
        Check(failureApi != null, "F5.TerminalFailureApi");
        if (failureApi != null)
        {
            foreach (GatewayOutcome outcome in new GatewayOutcome[] { GatewayOutcome.Denied,
                GatewayOutcome.Unprovisioned, GatewayOutcome.Timeout, GatewayOutcome.Malformed, GatewayOutcome.Cancelled })
            {
                for (int kind = 0; kind < 2; kind++)
                {
                    Stack stack = NewStack(740L + kind);
                    ResidentRecord resident = AddResident(stack.World, "Denied");
                    GatewayRequest request = kind == 0
                        ? stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true)
                        : stack.Boundary.RequestService(null, GatewayPurpose.ImageGeneration, ImgSvc(), true, true);
                    failureApi.Invoke(stack.Boundary, new object[] { request.Id, outcome });
                    Check(LogHas(stack, outcome, "Provider") && stack.Boundary.PendingCount == 0,
                        "F5.ProviderOutcome." + outcome.ToString() + "." + kind.ToString());
                    if (kind == 0) Check(CountOwner(stack, resident.Id, ActionOwner.Autonomous) == 1,
                        "F5.ProviderFallback." + outcome.ToString());
                    int count = stack.Boundary.DecisionLog.Count;
                    failureApi.Invoke(stack.Boundary, new object[] { request.Id, outcome });
                    Check(stack.Boundary.DecisionLog.Count == count, "F5.Idempotent." + outcome.ToString() + "." + kind.ToString());
                }
            }
            Stack paused = NewStack(745L);
            ResidentRecord heldResident = AddResident(paused.World, "PausedDenial");
            GatewayRequest denied = paused.Boundary.RequestDecision(heldResident.Id, D1U(true), true, true);
            paused.Clock.Pause();
            failureApi.Invoke(paused.Boundary, new object[] { denied.Id, GatewayOutcome.Denied });
            Check(paused.Arbitrator.GetPending(heldResident.Id).Length == 0, "F5.PausedDenialInert");
            paused.Clock.Resume();
            paused.Boundary.OnResume();
            Check(CountOwner(paused, heldResident.Id, ActionOwner.Autonomous) == 1, "F5.PausedDenialFallback");
            bool threw = false;
            try { failureApi.Invoke(paused.Boundary, new object[] { denied.Id, GatewayOutcome.Applied }); }
            catch (TargetInvocationException error) { threw = error.InnerException is ArgumentOutOfRangeException; }
            Check(threw, "F5.SuccessIsNotFailure");
        }
        {
            Stack stack = NewStack(750L);
            ServiceEndpoint service = ImgSvc();
            stack.Boundary.RequestService(null, GatewayPurpose.ImageGeneration, service, true, true);
            Check(stack.Boundary.RequestService(null, GatewayPurpose.VoiceTranscription, service, true, false) == null,
                "F6.NewPurposeNeedsNotice");
            Check(stack.Boundary.RequestService(null, GatewayPurpose.VoiceTranscription, service, true, true) != null,
                "F6.NewPurposeCanConsent");
            ServiceEndpoint moved = new ServiceEndpoint(service.Service, "https://other.invalid/v1", service.ModelId, true);
            Check(stack.Boundary.RequestService(null, GatewayPurpose.VoiceTranscription, moved, true, false) == null,
                "F6.NewEndpointNeedsNotice");
            Check(stack.Boundary.RequestService(null, GatewayPurpose.VoiceTranscription, service, true, false) != null,
                "F6.SameScopeRetainsNotice");
        }
        foreach (string model in new string[] { "muse-wonder-full-1.0", "muse-wonder-1.0", "muse-wonderfull" })
        {
            Stack stack = NewStack(760L);
            ResidentRecord resident = AddResident(stack.World, "DevelopmentOnly");
            ServiceEndpoint service = new ServiceEndpoint("dev", "https://api.meta.ai/v1", model, true);
            Check(stack.Boundary.RequestDecision(resident.Id, service, true, true) == null
                && stack.Boundary.RequestService(null, GatewayPurpose.VoiceTranscription, service, true, true) == null,
                "F7.DevelopmentModelRefused." + model);
        }

        if (failures == 0)
        {
            Console.WriteLine("AI-BOUNDARY ALL PASS");
        }
        return failures;
    }
}
