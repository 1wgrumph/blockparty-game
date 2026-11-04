/* D1U@1 Test-first contract. Product file:
Assets/Blockparty/AI/DecisionAdapter.cs, namespace Blockparty.AI:
  interface IDecisionTransport {
    void Send(GatewayRequest request, DecisionPayload payload);
    bool TryReceive(out DecisionResponse response);
  }
  sealed class DecisionResponse {
    DecisionResponse(GatewayRequestId requestId, GatewayOutcome outcome,
                     string activityCode, long? targetId);
  }
  sealed class DecisionAdapter {
    DecisionAdapter(ProposalBoundary boundary, IDecisionTransport transport);
    GatewayRequest RequestDecision(ResidentId resident, ServiceEndpoint service,
                                   bool hasCredential, bool disclosureGiven);
    void Tick(double elapsedRealSeconds);
  }
RequestDecision delegates issuance/refusal/payload minimization to ProposalBoundary;
only issued requests reach Send. Tick advances the boundary's real-time deadline,
drains transport responses and drives Tick/OnResume. Applied means a candidate
activity/target, never authority to mutate: use CompleteDecision. Other outcomes
use CompleteFailure. Empty receive means still pending, never fabricated success.
Deferred account access is ServiceEndpoint.Provisioned=false, with immediate local
fallback and no Send. Execution remains the existing executor/food tick's job.
No shared types change. No key parameter, key storage, credential lookup, credential logging,
UnityEngine or System.Net.Http dependency in the adapter. No network in this suite.

Oracles: REV-0 BP-AI-01..05, BP-SEC-07 and SEIT cases named on each check;
m2/test-engineer.json explicitly selects "Recorded/synthetic proposals" and the
injected failure classes. Fake only external I/O at the typed gateway seam, NOT
Liquid wire JSON. Vendor record d1u.json documents the endpoint/model/choice
schema but contains no observed wire response; provider compatibility is NOT RUN.
Real WorldState, ProposalBoundary, Autonomy, CommandArbitrator, ActivityExecutor
and Food are the consumers. ACT-FOOD-02 catalog delta is Hunger +40 on completion.
Known history is an actual Relationships Argument event; an unrelated event is not
resident knowledge. Personality stays local per the existing minimized payload:
Active=50 gives health candidates trait factor 1.5 (BP-SEC-05).
Resource group isolated-csharp; no editor, display, shared service or sleeps.
Whole SEIT cases remain pending their completion gates. Physical here means the
executor reaches Perform/Completed with exactly one committed effect and the
catalog need delta, not a rendered scene, live service or eight-resident acceptance.
*/
using System;
using System.Collections.Generic;
using System.IO;
using Blockparty.AI;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Simulation;

public static class D1UChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private const StateWriter Exec = StateWriter.ActivityExecutor;
    private static int failures;

    private sealed class Fixture
    {
        public WorldState World;
        public Clock Clock;
        public CommandArbitrator Arbitrator;
        public Autonomy Autonomy;
        public ProposalBoundary Boundary;
        public ActivityExecutor Executor;
        public Food Food;
        public ResidentRecord Resident;
        public EventId Known;
        public EventId Unknown;
#if !D1U_DEPENDENCY_CONTROL
        public FakeTransport Transport;
        public DecisionAdapter Adapter;
#endif
    }

#if !D1U_DEPENDENCY_CONTROL
    private sealed class FakeTransport : IDecisionTransport
    {
        public readonly List<GatewayRequest> Sent = new List<GatewayRequest>();
        public readonly List<DecisionPayload> Payloads = new List<DecisionPayload>();
        public readonly Queue<DecisionResponse> Replies = new Queue<DecisionResponse>();
        public void Send(GatewayRequest request, DecisionPayload payload)
        {
            Sent.Add(request);
            Payloads.Add(payload);
        }
        public bool TryReceive(out DecisionResponse response)
        {
            response = Replies.Count == 0 ? null : Replies.Dequeue();
            return response != null;
        }
    }

#endif

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    private static Fixture NewFixture()
    {
        var f = new Fixture();
        f.World = WorldState.CreateSeeded(7L, 42L, 0, null);
        var home = f.World.AllocateHome(Core, 8);
        var household = f.World.AllocateHousehold(Core, "D1U", home.Id);
        f.Resident = f.World.AllocateResident(Core, "Ada", LifeStage.Adult, household.Id, home.Id);
        var other = f.World.AllocateResident(Core, "Bo", LifeStage.Adult, household.Id, home.Id);
        for (int i = 0; i < StateSchema.NeedCount; i++)
            f.World.SetNeed(Core, f.Resident.Id, (Need)i, 80.0f);
        f.World.SetNeed(Core, f.Resident.Id, Need.Hunger, 8.0f);
        f.World.SetTrait(Core, f.Resident.Id, TraitAxis.Active, 50);
        var relations = new Relationships(f.World);
        var argument = relations.Complete(Core, new ActivityId("ACT-REL-04"), f.Resident.Id, other.Id,
            loserDraw: 0.0, initiatorSentimentDraw: 0.99, recipientSentimentDraw: 0.99);
        if (!argument.Applied || relations.Explain(f.Resident.Id, argument.Event) == null)
            throw new Exception("D1U fixture could not establish real known Argument history");
        f.Known = argument.Event;
        f.Unknown = f.World.AppendEvent(Core, "unobserved", other.Id, "not resident knowledge").Event;
        f.Clock = new Clock(f.World);
        f.Arbitrator = new CommandArbitrator(f.World, f.Clock);
        f.Autonomy = new Autonomy(f.World, f.Arbitrator);
        f.Boundary = new ProposalBoundary(f.World, f.Clock, f.Arbitrator, f.Autonomy);
        f.Executor = new ActivityExecutor(f.World, f.Arbitrator, f.Clock);
        f.Food = new Food(f.World, f.Arbitrator, f.Executor, new Accounts(f.World));
        f.Food.SetHomeStock(Core, home.Id, 4, 2);
#if !D1U_DEPENDENCY_CONTROL
        f.Transport = new FakeTransport();
        f.Adapter = new DecisionAdapter(f.Boundary, f.Transport);
#endif
        return f;
    }

#if !D1U_DEPENDENCY_CONTROL
    private static GatewayRequest Request(Fixture f, bool provisioned = true, bool credential = true)
    {
        // Public-doc endpoint string only; FakeTransport never opens it.
        return f.Adapter.RequestDecision(f.Resident.Id,
            new ServiceEndpoint("liquid-d1u", "https://api.liquid.ai/decisions/v1/systemone",
                "d1-ultra", provisioned), credential, true);
    }

    private static void Reply(Fixture f, GatewayRequest request, GatewayOutcome outcome,
        string code = "ACT-FOOD-02", long? target = null)
    {
        f.Transport.Replies.Enqueue(new DecisionResponse(request.Id, outcome, code, target));
        f.Adapter.Tick(0.0);
    }

#endif

    private static bool Logged(Fixture f, GatewayOutcome outcome, string reason)
    {
        foreach (var entry in f.Boundary.DecisionLog)
            if (entry.Outcome == outcome && entry.Detail.Contains(reason)) return true;
        return false;
    }

    private static ActionEntry NextAction(Fixture f)
    {
        var current = f.Arbitrator.GetCurrent(f.Resident.Id);
        var pending = f.Arbitrator.GetPending(f.Resident.Id);
        return current ?? (pending.Length == 0 ? null : pending[0]);
    }

    private static bool LocalConsistent(Fixture f)
    {
        if (f.Autonomy.Decisions.Count != 1) return false;
        var d = f.Autonomy.Decisions[0];
        var action = NextAction(f);
        return d.HasChoice && action != null && action.Owner == ActionOwner.Autonomous
            && action.Activity == d.Chosen && action.TargetId == d.ChosenTarget
            && d.CommittedEffect == "Enqueued:" + d.Chosen.Code
            && f.Arbitrator.GetCurrent(f.Resident.Id) == null
            && f.Arbitrator.GetPending(f.Resident.Id).Length == 1
            && f.World.EffectLedger.Count == 0 && f.Resident.GetNeed(Need.Hunger) == 8.0f;
    }

    private static CommandResult Player(Fixture f)
    {
        return f.Arbitrator.Submit(new CommandEnvelope(f.World.AllocateCommand(Core),
            f.World.Epoch, f.World.GameSeconds, CommandSource.PlayerHuman,
            f.Resident.ResidentSequence + 1u, CommandKind.DirectActivity, f.Resident.Id,
            new ActivityId("ACT-FOOD-02"), null, new CommandParameters.Direct(QueueMode.Immediate)));
    }

#if !D1U_DEPENDENCY_CONTROL
    private static void PhysicalAccepted()
    {
        var f = NewFixture();
        var request = Request(f);
        Check(request != null && f.Transport.Sent.Count == 1 && f.Transport.Sent[0] == request
            && request.Epoch == f.World.Epoch && request.Resident == f.Resident.Id
            && request.ContextVersion.Matches(f.Arbitrator.CurrentStamp(f.Resident.Id))
            && request.ModelId == "d1-ultra" && request.Purpose == GatewayPurpose.ResidentDecision
            && request.IssuedAtGameSeconds == f.World.GameSeconds,
            "SEIT-AI-02-P1.TransportCarriesBoundaryStamp.LocalOnly");
        if (request == null || f.Transport.Payloads.Count != 1) return;
        var payload = f.Transport.Payloads[0];
        Check(payload.Needs.Length == StateSchema.NeedCount && payload.Needs[(int)Need.Hunger] == 8.0f
            && Array.IndexOf(payload.AvailableActivityIds, "ACT-FOOD-02") >= 0
            && payload.KnownHistoryEventIds.Length == 1 && payload.KnownHistoryEventIds[0] == (long)f.Known
            && Array.IndexOf(payload.KnownHistoryEventIds, (long)f.Unknown) < 0
            && payload.Constraints.Length > 0,
            "SEIT-AI-05-P2.SEIT-AI-05-N3.ActualNeedAndFactualHistoryReachTransport.LocalOnly");
        Check(NextAction(f) == null && f.World.EffectLedger.Count == 0,
            "SEIT-AI-01-P1.PendingRequestHasNoActionOrEffects.LocalOnly");
        Reply(f, request, GatewayOutcome.Applied);
        var action = NextAction(f);
        Check(action != null && action.Owner == ActionOwner.ModelProposed
            && action.Activity.Code == "ACT-FOOD-02" && f.Autonomy.Decisions.Count == 0
            && f.Arbitrator.GetCurrent(f.Resident.Id) == null
            && f.Arbitrator.GetPending(f.Resident.Id).Length == 1
            && f.World.EffectLedger.Count == 0 && f.Resident.GetNeed(Need.Hunger) == 8.0f,
            "SEIT-IF-D1U-P1.ValidProposalEnqueuesWithoutEarlyReward.LocalOnly");
        if (action == null) return;
        PhysicalMeal(f, action);
        int hash = f.World.ComputeStateHash();
        Reply(f, request, GatewayOutcome.Applied);
        Check(f.World.ComputeStateHash() == hash && NextAction(f) == null,
            "SEIT-AI-02-N6.SEIT-SM-AI-I4.DuplicateCannotRepeatActionOrReward.LocalOnly");
    }

    private static void FailuresFallBack()
    {
        {
            var f = NewFixture();
            Request(f);
            Request(f);
            var third = Request(f);
            Check(third == null && f.Transport.Sent.Count == 2 && LocalConsistent(f)
                && Logged(f, GatewayOutcome.RateLimitedLocal, "RateLimited:DecisionPurpose"),
                "SEIT-AI-04-N5.SEIT-IF-D1U-N7.ConcurrencyRefusalFallsBackWithoutSending.LocalOnly");
        }
        // These failure classes are explicit m2 TE / BP-AI-04 fault inputs.
        foreach (var outcome in new[] { GatewayOutcome.Unprovisioned, GatewayOutcome.Timeout,
            GatewayOutcome.Denied, GatewayOutcome.Cancelled, GatewayOutcome.Malformed })
        {
            var f = NewFixture();
            Reply(f, Request(f), outcome, null);
            string id = outcome == GatewayOutcome.Timeout ? "SEIT-AI-04-N1"
                : outcome == GatewayOutcome.Denied ? "SEIT-AI-04-N2"
                : outcome == GatewayOutcome.Cancelled ? "SEIT-AI-04-N3"
                : outcome == GatewayOutcome.Malformed ? "SEIT-AI-04-N4" : "SEIT-IF-D1U-N5";
            Check(LocalConsistent(f) && f.Boundary.PendingCount == 0
                && Logged(f, outcome, "Provider:" + outcome) && Logged(f, outcome, "Fallback:Enqueued:"),
                id + ".SEIT-IF-D1U-N7." + outcome + "UsesRealLocalDecision.LocalOnly");
        }
        {
            var f = NewFixture();
            var request = Request(f);
            string canary = "Bearer " + new string('X', 32); // Synthetic response text, never a credential.
            var savedOutput = Console.Out;
            var captured = new StringWriter();
            try
            {
                Console.SetOut(captured);
                Reply(f, request, GatewayOutcome.Applied, canary);
            }
            finally { Console.SetOut(savedOutput); }
            bool clean = !captured.ToString().Contains(canary);
            foreach (var entry in f.Boundary.DecisionLog) clean &= !entry.Detail.Contains(canary);
            Check(clean && LocalConsistent(f),
                "SEIT-SECU-01-N1.SEIT-AI-01-N1.MalformedSecretShapedTextNeverLogged.LocalOnly");
        }
        foreach (string code in new[] { null, "execute arbitrary text", "ACT-NOT-OFFERED-99" })
        {
            var f = NewFixture();
            Reply(f, Request(f), GatewayOutcome.Applied, code);
            Check(LocalConsistent(f) && Logged(f, GatewayOutcome.Malformed, "Fallback:Enqueued:"),
                "SEIT-AI-01-N1.SEIT-AI-05-N1.SEIT-IF-D1U-N4.SEIT-SM-AI-I5.Reject."
                    + (code ?? "Empty") + ".LocalOnly");
        }
        // Offered codes have null targets; a foreign target is outside that offer.
        {
            var f = NewFixture();
            Reply(f, Request(f), GatewayOutcome.Applied, "ACT-FOOD-02", long.MaxValue);
            Check(LocalConsistent(f) && Logged(f, GatewayOutcome.Malformed, "Validation:"),
                "SEIT-AI-05-N2.SEIT-IF-D1U-N4.OutOfBoundsTargetFallsBack.LocalOnly");
        }
        foreach (bool deferred in new[] { true, false })
        {
            var f = NewFixture();
            var request = Request(f, !deferred, deferred);
            Check(request == null && f.Transport.Sent.Count == 0 && LocalConsistent(f)
                && Logged(f, GatewayOutcome.Unprovisioned,
                    deferred ? "AdapterNotProvisioned" : "MissingCredential"),
                "SEIT-IF-D1U-N5." + (deferred ? "DeferredAccess" : "AbsentCredential") + "NeverSends.LocalOnly");
        }
        {
            var f = NewFixture();
            f.World.SetNeed(Core, f.Resident.Id, Need.Hunger, 80.0f);
            f.World.SetNeed(Core, f.Resident.Id, Need.Hygiene, 8.0f);
            Request(f, false);
            var d = f.Autonomy.Decisions[0];
            bool trait = false;
            foreach (var candidate in d.Candidates)
                if (candidate.Activity.Code == "ACT-HEALTH-03")
                    trait = candidate.TraitFactor == 1.5 && candidate.Benefit > 0.0;
            Check(trait && d.HasChoice && d.Chosen.Code == "ACT-HEALTH-03"
                && NextAction(f).Activity == d.Chosen,
                "SEIT-AI-04-P1.LocalFallbackConnectsNeedAndPersonalityToChosenAction.LocalOnly");
        }
    }

    private static void TimingAndFreshness()
    {
        {
            var f = NewFixture();
            var request = Request(f);
            f.Adapter.Tick(9.0);
            Check(f.Boundary.PendingCount == 1 && f.Autonomy.Decisions.Count == 0,
                "SEIT-IF-D1U-N3.EmptyTransportWaitsWithinServiceDeadline.LocalOnly");
            f.Adapter.Tick(1.0);
            Check(LocalConsistent(f) && f.Boundary.PendingCount == 0
                && Logged(f, GatewayOutcome.Timeout, "ServiceTimeoutRealSeconds:10"),
                "SEIT-AI-04-N1.SEIT-IF-D1U-N3.TimeoutUsesLocalDecision.LocalOnly");
            var action = NextAction(f).Action;
            int hash = f.World.ComputeStateHash();
            Reply(f, request, GatewayOutcome.Applied);
            Check(LocalConsistent(f) && f.World.ComputeStateHash() == hash
                && NextAction(f).Action == action,
                "SEIT-AI-04-N6.SEIT-SM-AI-I6.LateReplyCannotReplaceFallback.LocalOnly");
        }
        {
            var f = NewFixture();
            Request(f);
            f.World.SetGameSeconds(Core, 180L);
            f.Adapter.Tick(0.0);
            Check(LocalConsistent(f) && Logged(f, GatewayOutcome.Timeout, "DecisionDeadlineGameMinutes:3"),
                "SEIT-IF-D1U-N3.GameDeadlineDoesNotWaitForRealTimeout.LocalOnly");
        }
        foreach (bool supersede in new[] { false, true })
        {
            var f = NewFixture();
            var request = Request(f);
            f.Clock.Pause();
            Reply(f, request, GatewayOutcome.Applied);
            Check(f.Boundary.HeldCount == 1 && NextAction(f) == null
                && f.World.EffectLedger.Count == 0,
                "SEIT-AI-03-N1.SEIT-IF-D1U-N2.SEIT-SM-AI-I1.PausedReplyHeld.LocalOnly");
            if (supersede) Check(Player(f).Accepted, "SEIT-AI-03-N2.PlayerCommandAcceptedDuringPause.LocalOnly");
            f.Clock.Resume();
            f.Adapter.Tick(0.0);
            var current = NextAction(f);
            Check(f.Boundary.HeldCount == 0 && current != null
                && current.Owner == (supersede ? ActionOwner.Player : ActionOwner.ModelProposed)
                && Logged(f, supersede ? GatewayOutcome.Stale : GatewayOutcome.Applied,
                    supersede ? "Stale:" : "Applied:"),
                "SEIT-IF-D1U-N2." + (supersede ? "SEIT-AI-03-N2.StaleOnResume" : "SEIT-AI-03-N1.FreshOnResume") + ".LocalOnly");
        }
        {
            var f = NewFixture();
            var request = Request(f);
            Check(Player(f).Accepted, "SEIT-IF-D1U-N1.PlayerCommandAccepted.LocalOnly");
            var player = NextAction(f).Action;
            Reply(f, request, GatewayOutcome.Applied);
            Check(Logged(f, GatewayOutcome.Stale, "Stale:")
                && NextAction(f).Action == player
                && f.Arbitrator.GetPending(f.Resident.Id).Length == 0 && f.World.EffectLedger.Count == 0,
                "SEIT-AI-02-N2.SEIT-IF-D1U-N1.SEIT-SM-AI-I2.LateReplyPreservesPlayerAction.LocalOnly");
        }
        {
            var f = NewFixture();
            var request = Request(f);
            f.World.TouchControlGeneration(Core, f.Resident.Id);
            Reply(f, request, GatewayOutcome.Applied);
            Check(Logged(f, GatewayOutcome.Stale, "Stale:Control") && LocalConsistent(f),
                "SEIT-AI-02-N3.SEIT-IF-D1U-N6.ControlGenerationRejectsReply.LocalOnly");
        }
        {
            var f = NewFixture();
            var request = Request(f);
            f.Boundary.NotifyWorldLoaded();
            int hash = f.World.ComputeStateHash();
            Reply(f, request, GatewayOutcome.Applied);
            Check(f.Boundary.PendingCount == 0 && NextAction(f) == null
                && f.World.ComputeStateHash() == hash && Logged(f, GatewayOutcome.Cancelled, "WorldLoad"),
                "SEIT-AI-03-N3.SEIT-IF-D1U-N6.SEIT-SM-AI-I3.LoadCancellationMakesLateReplyInert.LocalOnly");
        }
    }

    private sealed class ThrowingTransport : IDecisionTransport
    {
        public bool FailSend;
        public bool FailReceive;
        public readonly List<GatewayRequest> Sent = new List<GatewayRequest>();
        public void Send(GatewayRequest request, DecisionPayload payload)
        {
            if (FailSend) throw new IOException("injected unavailable transport");
            Sent.Add(request);
        }
        public bool TryReceive(out DecisionResponse response)
        {
            response = null;
            if (FailReceive) throw new IOException("injected unavailable transport");
            return false;
        }
    }

    private static void TransportExceptionsFallBack()
    {
        // M2-B1-R1: an unavailable transport must not escape the adapter;
        // the injected failure becomes a recorded terminal outcome and the
        // boundary deadline/local fallback still run.
        {
            var f = NewFixture();
            var adapter = new DecisionAdapter(f.Boundary,
                new ThrowingTransport { FailSend = true });
            bool threw = false;
            GatewayRequest request = null;
            try
            {
                request = adapter.RequestDecision(f.Resident.Id,
                    new ServiceEndpoint("liquid-d1u", "https://api.liquid.ai/decisions/v1/systemone",
                        "d1-ultra", true), true, true);
            }
            catch (Exception e)
            {
                threw = true;
                Console.WriteLine("TRANSPORT-RED send threw " + e.GetType().Name);
            }
            Check(!threw && request != null && f.Boundary.PendingCount == 0
                && LocalConsistent(f)
                && Logged(f, GatewayOutcome.Malformed, "Provider:Malformed")
                && Logged(f, GatewayOutcome.Malformed, "Fallback:Enqueued:"),
                "SEIT-AI-04-N4.SEIT-IF-D1U-N7.ThrowingSendCompletesAsMalformed.LocalOnly");
        }
        {
            var f = NewFixture();
            var request = Request(f);
            var adapter = new DecisionAdapter(f.Boundary,
                new ThrowingTransport { FailReceive = true });
            bool threw = false;
            for (int i = 0; i < 2; i++)
            {
                try { adapter.Tick(11.0); }
                catch (Exception e)
                {
                    threw = true;
                    Console.WriteLine("TRANSPORT-RED receive threw " + e.GetType().Name);
                }
            }
            Check(!threw && request != null && f.Boundary.PendingCount == 0
                && LocalConsistent(f)
                && Logged(f, GatewayOutcome.Timeout, "ServiceTimeoutRealSeconds:10"),
                "SEIT-AI-04-N1.SEIT-IF-D1U-N3.ThrowingReceiveKeepsDeadlineAndFallback.LocalOnly");
        }
    }

    private static void OutOfContractOutcomesFallBackAsMalformed()
    {
        // CompleteFailure accepts only Denied/Unprovisioned/Timeout/Malformed/
        // Cancelled; any other transport outcome is an invalid reply and must
        // complete as Malformed with Tick/OnResume still running. The held
        // proposal proves OnResume ran in the same Tick as the bad reply.
        foreach (GatewayOutcome outcome in new[] { GatewayOutcome.Stale,
            GatewayOutcome.RateLimitedLocal, (GatewayOutcome)999 })
        {
            var f = NewFixture();
            var held = Request(f);
            f.Clock.Pause();
            Reply(f, held, GatewayOutcome.Applied);
            f.Clock.Resume();
            var request = Request(f);
            f.Transport.Replies.Enqueue(new DecisionResponse(request.Id, outcome, null, null));
            bool threw = false;
            try { f.Adapter.Tick(0.0); }
            catch (Exception e)
            {
                threw = true;
                Console.WriteLine("OUTCOME-RED " + outcome + " threw " + e.GetType().Name);
            }
            // The Malformed fallback enqueues before OnResume, so the held
            // proposal resumes stale; HeldCount 1->0 plus its Stale log entry
            // proves OnResume ran in the same Tick as the bad reply.
            Check(!threw && request != null && f.Boundary.PendingCount == 0
                && f.Boundary.HeldCount == 0
                && f.Autonomy.Decisions.Count == 2
                && Logged(f, GatewayOutcome.Malformed, "Provider:Malformed")
                && Logged(f, GatewayOutcome.Malformed, "Fallback:Enqueued:")
                && Logged(f, GatewayOutcome.Stale, "Stale:"),
                "SEIT-AI-04-N4.SEIT-IF-D1U-N7." + outcome + "ReplyCompletesAsMalformed.LocalOnly");
        }
    }

#endif

    private static void PhysicalMeal(Fixture f, ActionEntry action)
    {
        f.Food.Tick(Exec);
        var execution = f.Executor.GetExecution(action.Action);
        Check(execution != null && execution.Stage == ExecutionStage.Perform && !execution.IsTerminal,
            "SEIT-AI-05-P1.RealExecutorPerformsAcceptedActivity.LocalOnly");
        // Catalog Eat duration is 20 game minutes. No needs-decay system is ticked.
        for (int tick = 0; tick < 200; tick++)
        {
            f.Clock.AdvanceRealSeconds(Core, 0.1);
            f.Food.Tick(Exec);
        }
        int committed = 0;
        foreach (var effect in f.World.EffectLedger)
            if (effect.Action == action.Action && effect.State == LedgerState.Committed) committed++;
        Check(execution != null && execution.Outcome == TerminalOutcome.Completed && committed == 1
            && f.Resident.GetNeed(Need.Hunger) == 48.0f
            && f.Food.PreparedMeals(f.Resident.Home) == 1
            && f.Arbitrator.GetCurrent(f.Resident.Id) == null,
            "SEIT-AI-05-P1.SEIT-IF-D1U-P1.PhysicalCompletionHasCatalogStateResult.LocalOnly");
    }

    private static void SourceContract()
    {
        const string path = "Assets/Blockparty/AI/DecisionAdapter.cs";
        Check(File.Exists(path), "SEIT-AI-01-P1.AdapterSourceExists.LocalOnly");
        if (!File.Exists(path)) return;
        string source = File.ReadAllText(path);
        Check(!source.Contains("using UnityEngine") && !source.Contains("UnityEngine.")
            && !source.Contains("System.Net.Http"),
            "SEIT-AI-01-P1.AdapterHasNoEngineOrHttpDependency.LocalOnly");
        bool keyFree = true;
        foreach (string forbidden in new[] { "KeyProvider", "KeyResolution", "KeySettings", "GetEnvironmentVariable",
            "ReadAllText", "ReadAllBytes", "Authorization" })
            if (source.Contains(forbidden)) keyFree = false;
        Check(keyFree, "SEIT-SECU-01-P2.AdapterHasNoCredentialReaderOrKeyType.SourceOnly");
    }

    public static int Main()
    {
#if D1U_DEPENDENCY_CONTROL
        // Preparation only: no stand-in adapter, no D1U pass claim.
        var f = NewFixture();
        var payload = f.Boundary.BuildDecisionPayload(f.Resident.Id);
        Check(payload.KnownHistoryEventIds.Length == 1 && payload.KnownHistoryEventIds[0] == (long)f.Known
            && payload.Needs[(int)Need.Hunger] == 8.0f,
            "SEIT-AI-05-N3.DependencyControl.RealFixtureNeedsAndHistory");
        f.Boundary.RequestDecision(f.Resident.Id,
            new ServiceEndpoint("fixture", "https://example.invalid", "d1-ultra", false), false, true);
        Check(LocalConsistent(f), "SEIT-AI-04-P1.DependencyControl.RealAutonomyFallback");
        var personality = NewFixture();
        personality.World.SetNeed(Core, personality.Resident.Id, Need.Hunger, 80.0f);
        personality.World.SetNeed(Core, personality.Resident.Id, Need.Hygiene, 8.0f);
        var choice = personality.Autonomy.Decide(personality.Resident.Id);
        Check(choice.Chosen.Code == "ACT-HEALTH-03" && choice.Candidates.Count == 1
            && choice.Candidates[0].TraitFactor == 1.5,
            "SEIT-AI-04-P1.DependencyControl.NeedAndTraitOracle");
        var physical = NewFixture();
        var request = physical.Boundary.RequestDecision(physical.Resident.Id,
            new ServiceEndpoint("fixture", "https://example.invalid", "d1-ultra", true), true, true);
        Check(physical.Boundary.CompleteDecision(request.Id, "ACT-FOOD-02", null) == ProposalVerdict.Applied,
            "SEIT-IF-D1U-P1.DependencyControl.RealBoundaryAcceptsMeal");
        PhysicalMeal(physical, NextAction(physical));
        if (failures == 0) Console.WriteLine("D1U DEPENDENCY CONTROL PASS (adapter not exercised)");
#else
        PhysicalAccepted();
        FailuresFallBack();
        TimingAndFreshness();
        OutOfContractOutcomesFallBackAsMalformed();
        TransportExceptionsFallBack();
        SourceContract();
        if (failures == 0) Console.WriteLine("D1U ALL PASS");
#endif
        return failures == 0 ? 0 : 1;
    }
}
