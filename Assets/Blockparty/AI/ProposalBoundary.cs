// AI-BOUNDARY@1: the typed async AI proposal boundary (AI-GATEWAY, CONTRACT-BP-04).
// Consumes IFC-LOCK@1 (GatewayRequest, DecisionProposal, FreshnessStamp,
// GatewayPurpose/Outcome/Limits, ActionContextSnapshot), COMMAND@1
// (CommandArbitrator stamps, snapshot, ApplyProposal), PERSONALITY@1 (Autonomy
// local fallback) and SAVE@1 (world load cancels pending: LoadFromSnapshot
// bumps WorldEpoch and rebuilds a fresh stack, so the new boundary starts
// empty; NotifyWorldLoaded cancels the stale boundary's pendings). Plain C# 9,
// no UnityEngine dependency. No network, no credential material: requests take
// a provisioned/configured endpoint plus a hasCredential boolean, never a key.
// Request ids allocate through WorldState with StateWriter.CommandArbitrator
// (a dedicated AiGateway writer belongs to a STATE follow-up; proposals land
// through the command path). Decision log is this instance's runtime state,
// like arbitrator queues and autonomy decisions; SAVE persists it later.
// Fast Enter Play Mode: no static mutable state, explicit numeric conversions,
// no AppDomain, Assembly.Location, BinaryFormatter, wall-clock reads or
// String.GetHashCode. Real-time deadlines accrue only through the explicit
// AdvanceRealSeconds input, so speed changes can never extend them.
// Normative source: BP-AI-01 (typed proposals, logged rejections), BP-AI-02
// (request identity + freshness), BP-AI-03 (async/pause/load), BP-AI-04
// (timeout/denied/cancelled/malformed fallback), BP-AI-14 (no dev models at
// runtime), BP-SECU-02 (minimized payloads, first-send disclosure) and the
// BP-SEC-07 AI gateway contract.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;
using Blockparty.Simulation;

namespace Blockparty.AI
{
    // One named adapter's configuration: endpoint, model id and access status
    // are values, never code. No credential material lives here or anywhere
    // in this package: the caller holds the player-supplied key (owner
    // decision 2026-10-27) and passes only its presence.
    public sealed class ServiceEndpoint
    {
        public readonly string Service;
        public readonly string Endpoint;
        public readonly string ModelId;
        public readonly bool Provisioned;

        public ServiceEndpoint(string service, string endpoint, string modelId, bool provisioned)
        {
            if (string.IsNullOrEmpty(service))
            {
                throw new ArgumentException("Service name must be non-empty", "service");
            }
            if (string.IsNullOrEmpty(endpoint))
            {
                throw new ArgumentException("Endpoint must be non-empty", "endpoint");
            }
            if (string.IsNullOrEmpty(modelId))
            {
                throw new ArgumentException("Model id must be non-empty", "modelId");
            }
            Service = service;
            Endpoint = endpoint;
            ModelId = modelId;
            Provisioned = provisioned;
        }
    }

    // How one completion, cancellation or refusal resolved. Applied means the
    // typed proposal passed freshness and simulation validation and landed;
    // every other terminal verdict took the defined local path or none.
    public enum ProposalVerdict
    {
        Applied,
        HeldForPause,
        DiscardedStale,
        DiscardedInvalid,
        DuplicateIgnored,
        Refused,
        Cancelled,
    }

    // One decision-log record (BP-SEC-07 ring): request id, model id, purpose,
    // outcome class and minimal validated choice data or rejection reason.
    // Detail never carries credentials or raw microphone/image payloads: no
    // such value reaches this package.
    public sealed class ProposalLogEntry
    {
        public readonly long Sequence;
        public readonly long GameSeconds;
        public readonly long RequestId;
        public readonly string ModelId;
        public readonly GatewayPurpose Purpose;
        public readonly GatewayOutcome Outcome;
        public readonly string Detail;

        public ProposalLogEntry(long sequence, long gameSeconds, long requestId,
            string modelId, GatewayPurpose purpose, GatewayOutcome outcome, string detail)
        {
            Sequence = sequence;
            GameSeconds = gameSeconds;
            RequestId = requestId;
            ModelId = modelId ?? string.Empty;
            Purpose = purpose;
            Outcome = outcome;
            Detail = detail ?? string.Empty;
        }
    }

    // Minimized resident-decision payload (BP-AI-05, BP-SECU-02): observable
    // needs, available activity ids, personally known history event ids and
    // constraints only. Display names, household, money, family, raw memory
    // text and raw feelings have no field here, so they cannot be sent.
    public sealed class DecisionPayload
    {
        public readonly float[] Needs;
        public readonly string[] AvailableActivityIds;
        public readonly long[] KnownHistoryEventIds;
        public readonly string Constraints;

        public DecisionPayload(float[] needs, string[] availableActivityIds,
            long[] knownHistoryEventIds, string constraints)
        {
            Needs = needs ?? new float[0];
            AvailableActivityIds = availableActivityIds ?? new string[0];
            KnownHistoryEventIds = knownHistoryEventIds ?? new long[0];
            Constraints = constraints ?? string.Empty;
        }
    }

    public sealed class ProposalBoundary
    {
        private const StateWriter Writer = StateWriter.CommandArbitrator;
        private const string DecisionConstraints =
            "BoundedChoice:offered-set-only;Freshness:epoch/sequence/slot/control";

        private sealed class PendingEntry
        {
            public readonly GatewayRequest Request;
            public readonly ServiceEndpoint Service;
            public readonly IReadOnlyList<AvailableAction> Offered;
            public readonly double IssuedRealSeconds;
            public readonly int TimeoutRealSeconds;
            public readonly bool IsDecision;

            public PendingEntry(GatewayRequest request, ServiceEndpoint service,
                IReadOnlyList<AvailableAction> offered, double issuedRealSeconds,
                int timeoutRealSeconds, bool isDecision)
            {
                Request = request;
                Service = service;
                Offered = offered;
                IssuedRealSeconds = issuedRealSeconds;
                TimeoutRealSeconds = timeoutRealSeconds;
                IsDecision = isDecision;
            }
        }

        private sealed class HeldEntry
        {
            public readonly PendingEntry Pending;
            public readonly DecisionProposal Proposal;

            public HeldEntry(PendingEntry pending, DecisionProposal proposal)
            {
                Pending = pending;
                Proposal = proposal;
            }
        }

        private readonly WorldState world;
        private readonly Clock clock;
        private readonly CommandArbitrator arbitrator;
        private readonly Autonomy autonomy;
        private readonly Dictionary<GatewayRequestId, PendingEntry> pending =
            new Dictionary<GatewayRequestId, PendingEntry>();
        private readonly Dictionary<GatewayRequestId, HeldEntry> held =
            new Dictionary<GatewayRequestId, HeldEntry>();
        private readonly Dictionary<string, string> disclosed =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<(string Service, GatewayPurpose Purpose), string> disclosedEndpoints =
            new Dictionary<(string Service, GatewayPurpose Purpose), string>();
        private readonly HashSet<ResidentId> deferredFallbacks = new HashSet<ResidentId>();
        private readonly List<ProposalLogEntry> log = new List<ProposalLogEntry>();
        private double elapsedRealSeconds;
        private long nextSequence = 1;
        private int truncatedRecords;

        public ProposalBoundary(WorldState world, Clock clock,
            CommandArbitrator arbitrator, Autonomy autonomy)
        {
            if (world == null)
            {
                throw new ArgumentNullException("world");
            }
            if (clock == null)
            {
                throw new ArgumentNullException("clock");
            }
            if (arbitrator == null)
            {
                throw new ArgumentNullException("arbitrator");
            }
            if (autonomy == null)
            {
                throw new ArgumentNullException("autonomy");
            }
            this.world = world;
            this.clock = clock;
            this.arbitrator = arbitrator;
            this.autonomy = autonomy;
        }

        public WorldState World { get { return world; } }
        public int PendingCount { get { return pending.Count; } }
        public bool IsPending(GatewayRequestId id) { return pending.ContainsKey(id); }
        public int HeldCount { get { return held.Count; } }
        public double ElapsedRealSeconds { get { return elapsedRealSeconds; } }
        public long NextSequence { get { return nextSequence; } }
        public int TruncatedRecords { get { return truncatedRecords; } }
        public IReadOnlyList<ProposalLogEntry> DecisionLog { get { return log.AsReadOnly(); } }

        // Real-time deadline input. The only clock this boundary reads; tests
        // and the game loop feed measured real seconds explicitly.
        public void AdvanceRealSeconds(double realSeconds)
        {
            if (double.IsNaN(realSeconds) || double.IsInfinity(realSeconds) || realSeconds < 0.0)
            {
                throw new ArgumentOutOfRangeException("realSeconds", "Advance amount must be finite and non-negative");
            }
            elapsedRealSeconds += realSeconds;
        }

        public bool IsFirstSendDisclosed(string service)
        {
            if (service == null)
            {
                throw new ArgumentNullException("service");
            }
            return disclosed.ContainsKey(service);
        }

        public string DisclosedWhat(string service)
        {
            if (service == null)
            {
                throw new ArgumentNullException("service");
            }
            string what;
            if (disclosed.TryGetValue(service, out what))
            {
                return what;
            }
            return string.Empty;
        }

        // Records the BP-SECU-02 first-send notice: what leaves the device for
        // which named service. The field list is fixed per purpose below, so
        // resident data can never leak into the notice itself. This string
        // overload records UI text only; the endpoint overload binds send scope.
        public void RecordFirstSendDisclosure(string service, GatewayPurpose purpose)
        {
            if (service == null)
            {
                throw new ArgumentNullException("service");
            }
            disclosed[service] = DisclosureLabel(purpose);
        }

        // A send notice covers one payload purpose at one destination.
        public void RecordFirstSendDisclosure(ServiceEndpoint service, GatewayPurpose purpose)
        {
            if (service == null) throw new ArgumentNullException("service");
            RecordFirstSendDisclosure(service.Service, purpose);
            disclosedEndpoints[(service.Service, purpose)] = service.Endpoint;
        }

        // Issues one resident-decision request stamped per BP-AI-02. Returns
        // the request when it is sent (pending); returns null when it is not
        // sent, in which case the outcome class is logged and the local
        // fallback runs at once unless the player holds control.
        public GatewayRequest RequestDecision(ResidentId resident, ServiceEndpoint service,
            bool hasCredential, bool disclosureGiven)
        {
            if (service == null)
            {
                throw new ArgumentNullException("service");
            }
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record))
            {
                throw new ArgumentException("Unknown resident " + ((long)resident).ToString(), "resident");
            }
            if (!arbitrator.AutomationEnabled)
            {
                AppendLog(0L, service.ModelId, GatewayPurpose.ResidentDecision,
                    GatewayOutcome.Denied, "AutomationDisabled;FallbackSkipped:AutomationDisabled");
                return null;
            }
            string refusal = RefusalReason(service, hasCredential, disclosureGiven,
                GatewayPurpose.ResidentDecision, true);
            if (refusal != null)
            {
                GatewayOutcome outcome = refusal.StartsWith("RateLimited", StringComparison.Ordinal)
                    ? GatewayOutcome.RateLimitedLocal
                    : (refusal.StartsWith("Unprovisioned", StringComparison.Ordinal)
                        ? GatewayOutcome.Unprovisioned : GatewayOutcome.Denied);
                AppendLog(0L, service.ModelId, GatewayPurpose.ResidentDecision,
                    outcome, refusal + ";" + FallbackDetail(resident, true));
                return null;
            }
            if (!record.IsAlive)
            {
                AppendLog(0L, service.ModelId, GatewayPurpose.ResidentDecision,
                    GatewayOutcome.Denied, "ResidentDeceased;FallbackSkipped:Deceased");
                return null;
            }
            ActionContextSnapshot snapshot = arbitrator.Snapshot(resident);
            GatewayRequest request = new GatewayRequest(
                world.AllocateGatewayRequest(Writer),
                world.Epoch,
                resident,
                snapshot.Version,
                service.ModelId,
                world.GameSeconds,
                GatewayPurpose.ResidentDecision);
            pending[request.Id] = new PendingEntry(request, service,
                snapshot.Available, elapsedRealSeconds,
                GatewayLimits.ServiceTimeoutRealSeconds, true);
            return request;
        }

        // Issues one adapter-owned request (image, voice, SAM): same stamping,
        // disclosure and rate-limit gates, but no local decision exists here,
        // so a refusal only logs and the caller takes its defined alternative.
        // Residentless purposes stamp the world-command sequence. The adapter
        // supplies its purpose deadline; the public default remains 10 seconds.
        public GatewayRequest RequestService(ResidentId? resident, GatewayPurpose purpose,
            ServiceEndpoint service, bool hasCredential, bool disclosureGiven)
        {
            return RequestService(resident, purpose, service, hasCredential,
                disclosureGiven, GatewayLimits.ServiceTimeoutRealSeconds);
        }

        internal GatewayRequest RequestService(ResidentId? resident, GatewayPurpose purpose,
            ServiceEndpoint service, bool hasCredential, bool disclosureGiven,
            int timeoutRealSeconds)
        {
            if (service == null)
            {
                throw new ArgumentNullException("service");
            }
            if (timeoutRealSeconds <= 0)
            {
                throw new ArgumentOutOfRangeException("timeoutRealSeconds");
            }
            if (purpose == GatewayPurpose.ResidentDecision)
            {
                throw new ArgumentException("Resident decisions use RequestDecision", "purpose");
            }
            if (resident.HasValue)
            {
                ResidentRecord record;
                if (!world.TryGetResident(resident.Value, out record))
                {
                    throw new ArgumentException("Unknown resident " + ((long)resident.Value).ToString(), "resident");
                }
            }
            string refusal = RefusalReason(service, hasCredential, disclosureGiven, purpose, false);
            if (refusal != null)
            {
                GatewayOutcome outcome = refusal.StartsWith("RateLimited", StringComparison.Ordinal)
                    ? GatewayOutcome.RateLimitedLocal
                    : (refusal.StartsWith("Unprovisioned", StringComparison.Ordinal)
                        ? GatewayOutcome.Unprovisioned : GatewayOutcome.Denied);
                AppendLog(0L, service.ModelId, purpose, outcome, refusal);
                return null;
            }
            FreshnessStamp stamp = resident.HasValue
                ? arbitrator.CurrentStamp(resident.Value)
                : new FreshnessStamp(world.Epoch, arbitrator.WorldSequence, 0,
                    default(HomeId), 0u, 0u);
            GatewayRequest request = new GatewayRequest(
                world.AllocateGatewayRequest(Writer),
                world.Epoch,
                resident,
                stamp,
                service.ModelId,
                world.GameSeconds,
                purpose);
            pending[request.Id] = new PendingEntry(request, service, null,
                elapsedRealSeconds, timeoutRealSeconds, false);
            return request;
        }

        // Delivers one raw decision response: typed parsing first (BP-AI-01),
        // then pause hold, then freshness plus simulation validation. Unknown
        // or already finalized ids are idempotent no-ops: out-of-order and
        // duplicate responses resolve by request id, never arrival order.
        public ProposalVerdict CompleteDecision(GatewayRequestId id, string activityCode, long? targetId)
        {
            PendingEntry entry;
            if (!pending.TryGetValue(id, out entry) || !entry.IsDecision)
            {
                return ProposalVerdict.DuplicateIgnored;
            }
            if (FinalizeExpired(entry))
            {
                pending.Remove(id);
                return ProposalVerdict.Refused;
            }
            string parseError = ParseError(activityCode);
            if (parseError != null)
            {
                pending.Remove(id);
                AppendLog((long)id, entry.Service.ModelId, GatewayPurpose.ResidentDecision,
                    GatewayOutcome.Malformed,
                    parseError + ";" + FallbackDetail(entry.Request.Resident.Value, true));
                return ProposalVerdict.DiscardedInvalid;
            }
            DecisionProposal proposal = new DecisionProposal(id,
                new ActivityId(activityCode), targetId, entry.Request.ContextVersion);
            if (clock.Paused)
            {
                pending.Remove(id);
                held[id] = new HeldEntry(entry, proposal);
                return ProposalVerdict.HeldForPause;
            }
            pending.Remove(id);
            return ApplyDecision(entry, proposal);
        }

        // Delivers one adapter-owned response. The adapter parses its own
        // typed result; parsed=false carries a fixed reason code (never raw
        // payload). True responses while paused are held for resume. Returns
        // the request when it applied, else null; HeldForPause keeps the
        // request held for RevalidateHeldService after resume.
        public ProposalVerdict CompleteService(GatewayRequestId id, bool parsed, string rejectionReason)
        {
            PendingEntry entry;
            if (!pending.TryGetValue(id, out entry) || entry.IsDecision)
            {
                return ProposalVerdict.DuplicateIgnored;
            }
            if (FinalizeExpired(entry))
            {
                pending.Remove(id);
                return ProposalVerdict.Refused;
            }
            if (!parsed)
            {
                pending.Remove(id);
                AppendLog((long)id, entry.Service.ModelId, entry.Request.Purpose,
                    GatewayOutcome.Malformed,
                    "Parse:" + (rejectionReason ?? string.Empty));
                return ProposalVerdict.DiscardedInvalid;
            }
            if (clock.Paused)
            {
                pending.Remove(id);
                held[id] = new HeldEntry(entry, null);
                return ProposalVerdict.HeldForPause;
            }
            pending.Remove(id);
            return ApplyService(entry);
        }

        // Provider failures are terminal outcomes, not malformed success payloads.
        public ProposalVerdict CompleteFailure(GatewayRequestId id, GatewayOutcome outcome)
        {
            if (outcome != GatewayOutcome.Denied && outcome != GatewayOutcome.Unprovisioned
                && outcome != GatewayOutcome.Timeout && outcome != GatewayOutcome.Malformed
                && outcome != GatewayOutcome.Cancelled)
            {
                throw new ArgumentOutOfRangeException("outcome");
            }
            PendingEntry entry;
            if (!pending.TryGetValue(id, out entry)) return ProposalVerdict.DuplicateIgnored;
            pending.Remove(id);
            if (FinalizeExpired(entry)) return ProposalVerdict.Refused;
            string detail = "Provider:" + outcome.ToString();
            if (entry.IsDecision) detail += ";" + FallbackDetail(entry.Request.Resident.Value, true);
            AppendLog((long)id, entry.Service.ModelId, entry.Request.Purpose, outcome, detail);
            return outcome == GatewayOutcome.Cancelled ? ProposalVerdict.Cancelled
                : outcome == GatewayOutcome.Malformed ? ProposalVerdict.DiscardedInvalid : ProposalVerdict.Refused;
        }

        // Takes one response held during pause: fresh requests apply, stale
        // ones are discarded with the mismatch logged. Decisions apply
        // themselves on resume through OnResume; adapters call this when they
        // are ready to present their typed result.
        public GatewayRequest RevalidateHeldService(GatewayRequestId id)
        {
            if (clock.Paused) return null;
            HeldEntry found;
            if (!held.TryGetValue(id, out found) || found.Proposal != null)
            {
                return null;
            }
            held.Remove(id);
            if (CancelIfDeceased(found.Pending)) return null;
            string mismatch = FreshnessMismatch(found.Pending);
            if (mismatch != null)
            {
                AppendLog((long)id, found.Pending.Service.ModelId, found.Pending.Request.Purpose,
                    GatewayOutcome.Stale, mismatch);
                return null;
            }
            AppendLog((long)id, found.Pending.Service.ModelId, found.Pending.Request.Purpose,
                GatewayOutcome.Applied, "RevalidatedOnResume");
            return found.Pending.Request;
        }

        // Player cancellation of the initiating feature (BP-SEC-07): the
        // pending or held request is recorded Cancelled and a decision cancel
        // runs the local fallback unless the player holds control.
        public bool Cancel(GatewayRequestId id)
        {
            PendingEntry entry;
            if (pending.TryGetValue(id, out entry))
            {
                pending.Remove(id);
                CancelEntry(entry);
                return true;
            }
            HeldEntry found;
            if (held.TryGetValue(id, out found))
            {
                held.Remove(id);
                CancelEntry(found.Pending);
                return true;
            }
            return false;
        }

        // Finalizes expired pendings: each adapter's supplied real-time timeout
        // (10 seconds unless it chose otherwise) plus the 3-game-minute decision
        // deadline for resident decisions, and cancellation for residents who
        // died while pending.
        // While paused nothing is finalized: the deadline accrues but
        // application waits for resume. Returns the finalized count.
        public int Tick()
        {
            if (clock.Paused)
            {
                return 0;
            }
            List<GatewayRequestId> ids = new List<GatewayRequestId>(pending.Keys);
            ids.Sort(CompareRequestIds);
            int finalized = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                PendingEntry entry;
                if (!pending.TryGetValue(ids[i], out entry))
                {
                    continue;
                }
                if (FinalizeExpired(entry))
                {
                    pending.Remove(ids[i]);
                    finalized++;
                }
            }
            ids = new List<GatewayRequestId>(held.Keys);
            ids.Sort(CompareRequestIds);
            for (int i = 0; i < ids.Count; i++)
            {
                if (CancelIfDeceased(held[ids[i]].Pending))
                {
                    held.Remove(ids[i]);
                    finalized++;
                }
            }
            return finalized;
        }

        // Resume path (call after the clock resumes): finalizes anything that
        // expired during pause, auto-applies held decisions in request-id
        // order, and discards held adapter responses that went stale (fresh
        // ones stay held for RevalidateHeldService).
        public void OnResume()
        {
            if (clock.Paused) return;
            Tick();
            List<GatewayRequestId> ids = new List<GatewayRequestId>(held.Keys);
            ids.Sort(CompareRequestIds);
            for (int i = 0; i < ids.Count; i++)
            {
                HeldEntry found;
                if (!held.TryGetValue(ids[i], out found))
                {
                    continue;
                }
                held.Remove(ids[i]);
                if (found.Proposal != null)
                {
                    ApplyDecision(found.Pending, found.Proposal);
                }
                else
                {
                    string mismatch = FreshnessMismatch(found.Pending);
                    if (mismatch != null)
                    {
                        AppendLog((long)ids[i], found.Pending.Service.ModelId,
                            found.Pending.Request.Purpose, GatewayOutcome.Stale, mismatch);
                    }
                    else
                    {
                        held[ids[i]] = found;
                    }
                }
            }
            List<ResidentId> residents = new List<ResidentId>(deferredFallbacks);
            residents.Sort((a, b) => ((long)a).CompareTo((long)b));
            deferredFallbacks.Clear();
            for (int i = 0; i < residents.Count; i++)
            {
                FallbackDetail(residents[i], true);
            }
        }

        // World-load path (BP-AI-03): every pending and held request is
        // recorded Cancelled. No fallback runs: the replacement world decides
        // fresh at its next idle tick through the normal path.
        public void NotifyWorldLoaded()
        {
            List<GatewayRequestId> ids = new List<GatewayRequestId>(pending.Keys);
            ids.Sort(CompareRequestIds);
            for (int i = 0; i < ids.Count; i++)
            {
                PendingEntry entry = pending[ids[i]];
                AppendLog((long)ids[i], entry.Service.ModelId, entry.Request.Purpose,
                    GatewayOutcome.Cancelled, "WorldLoaded;FallbackSkipped:Load");
            }
            deferredFallbacks.Clear();
            pending.Clear();
            List<GatewayRequestId> heldIds = new List<GatewayRequestId>(held.Keys);
            heldIds.Sort(CompareRequestIds);
            for (int i = 0; i < heldIds.Count; i++)
            {
                HeldEntry found = held[heldIds[i]];
                AppendLog((long)heldIds[i], found.Pending.Service.ModelId,
                    found.Pending.Request.Purpose, GatewayOutcome.Cancelled,
                    "WorldLoaded;FallbackSkipped:Load");
            }
            held.Clear();
        }

        // Builds the minimized decision payload: needs, offered activity ids,
        // personally known history event ids (this resident's own memory list)
        // and constraints. Nothing else exists to send.
        public DecisionPayload BuildDecisionPayload(ResidentId resident)
        {
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record))
            {
                throw new ArgumentException("Unknown resident " + ((long)resident).ToString(), "resident");
            }
            float[] needs = new float[StateSchema.NeedCount];
            for (int i = 0; i < needs.Length; i++)
            {
                needs[i] = record.GetNeed((Need)i);
            }
            ActionContextSnapshot snapshot = arbitrator.Snapshot(resident);
            string[] codes = new string[snapshot.Available.Count];
            for (int i = 0; i < codes.Length; i++)
            {
                codes[i] = snapshot.Available[i].Activity.Code;
            }
            long[] history = new long[record.Memories.Count];
            for (int i = 0; i < history.Length; i++)
            {
                history[i] = (long)record.Memories[i].Event;
            }
            return new DecisionPayload(needs, codes, history, DecisionConstraints);
        }

        private ProposalVerdict ApplyDecision(PendingEntry entry, DecisionProposal proposal)
        {
            if (CancelIfDeceased(entry)) return ProposalVerdict.Cancelled;
            ResidentId resident = entry.Request.Resident.Value;
            string mismatch = FreshnessMismatch(entry);
            if (mismatch != null)
            {
                AppendLog((long)entry.Request.Id, entry.Service.ModelId,
                    GatewayPurpose.ResidentDecision, GatewayOutcome.Stale,
                    mismatch + ";" + FallbackDetail(resident, true));
                return ProposalVerdict.DiscardedStale;
            }
            CommandResult result = arbitrator.ApplyProposal(resident, proposal, entry.Offered);
            if (result.Accepted)
            {
                AppendLog((long)entry.Request.Id, entry.Service.ModelId,
                    GatewayPurpose.ResidentDecision, GatewayOutcome.Applied,
                    "Applied:" + proposal.Activity.Code);
                return ProposalVerdict.Applied;
            }
            if (result.Reason == RejectReason.StaleEpoch || result.Reason == RejectReason.StaleGeneration)
            {
                AppendLog((long)entry.Request.Id, entry.Service.ModelId,
                    GatewayPurpose.ResidentDecision, GatewayOutcome.Stale,
                    "Apply:" + result.Reason.ToString() + ";" + FallbackDetail(resident, true));
                return ProposalVerdict.DiscardedStale;
            }
            AppendLog((long)entry.Request.Id, entry.Service.ModelId,
                GatewayPurpose.ResidentDecision, GatewayOutcome.Malformed,
                "Validation:" + result.Reason.ToString() + ";" + FallbackDetail(resident, true));
            return ProposalVerdict.DiscardedInvalid;
        }

        private ProposalVerdict ApplyService(PendingEntry entry)
        {
            if (CancelIfDeceased(entry)) return ProposalVerdict.Cancelled;
            string mismatch = FreshnessMismatch(entry);
            if (mismatch != null)
            {
                AppendLog((long)entry.Request.Id, entry.Service.ModelId, entry.Request.Purpose,
                    GatewayOutcome.Stale, mismatch);
                return ProposalVerdict.DiscardedStale;
            }
            AppendLog((long)entry.Request.Id, entry.Service.ModelId, entry.Request.Purpose,
                GatewayOutcome.Applied, "Validated");
            return ProposalVerdict.Applied;
        }

        private bool CancelIfDeceased(PendingEntry entry)
        {
            if (!entry.Request.Resident.HasValue) return false;
            ResidentRecord record;
            if (world.TryGetResident(entry.Request.Resident.Value, out record) && record.IsAlive) return false;
            AppendLog((long)entry.Request.Id, entry.Service.ModelId, entry.Request.Purpose,
                GatewayOutcome.Cancelled, "ResidentDeceased;FallbackSkipped:Deceased");
            return true;
        }

        private bool FinalizeExpired(PendingEntry entry)
        {
            if (CancelIfDeceased(entry)) return true;
            string detail = null;
            if (elapsedRealSeconds - entry.IssuedRealSeconds
                >= (double)entry.TimeoutRealSeconds)
                detail = "ServiceTimeoutRealSeconds:" + entry.TimeoutRealSeconds.ToString();
            else if (entry.IsDecision && world.GameSeconds - entry.Request.IssuedAtGameSeconds
                >= (long)GatewayLimits.DecisionFallbackGameMinutes * 60L)
                detail = "DecisionDeadlineGameMinutes:" + GatewayLimits.DecisionFallbackGameMinutes.ToString();
            if (detail == null) return false;
            FinalizeTimeout(entry, detail);
            return true;
        }

        private void CancelEntry(PendingEntry entry)
        {
            string detail = "PlayerCancel";
            if (entry.IsDecision)
            {
                detail += ";" + FallbackDetail(entry.Request.Resident.Value, true);
            }
            AppendLog((long)entry.Request.Id, entry.Service.ModelId, entry.Request.Purpose,
                GatewayOutcome.Cancelled, detail);
        }

        private void FinalizeTimeout(PendingEntry entry, string detail)
        {
            if (entry.IsDecision)
            {
                detail += ";" + FallbackDetail(entry.Request.Resident.Value, true);
            }
            AppendLog((long)entry.Request.Id, entry.Service.ModelId, entry.Request.Purpose,
                GatewayOutcome.Timeout, detail);
        }

        // Runs the PERSONALITY local fallback for one resident and renders the
        // outcome detail. Skipped exactly when the player holds control or the
        // resident is gone: then there is nobody to decide for.
        private string FallbackDetail(ResidentId resident, bool allowFallback)
        {
            if (!allowFallback || !arbitrator.AutomationEnabled)
            {
                return "FallbackSkipped:AutomationDisabled";
            }
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record) || !record.IsAlive)
            {
                return "FallbackSkipped:Deceased";
            }
            if (clock.Paused)
            {
                deferredFallbacks.Add(resident);
                return "FallbackDeferred:Paused";
            }
            AutonomyDecision decision = autonomy.Decide(resident);
            if (decision.HasChoice)
            {
                return "Fallback:" + decision.CommittedEffect;
            }
            return "Fallback:Idle:" + decision.IdleReason;
        }

        // Refusal gates shared by both request paths, in contract order:
        // prohibited runtime models (BP-AI-14), provisioning and credential
        // presence (BP-AI-04/BP-AI-13), first-send disclosure (BP-SECU-02) and
        // concurrency limits (BP-SEC-07). Returns null when the request sends.
        private string RefusalReason(ServiceEndpoint service, bool hasCredential,
            bool disclosureGiven, GatewayPurpose purpose, bool isDecision)
        {
            if (IsProhibitedRuntimeModel(service.ModelId))
            {
                return "Denied:ProhibitedRuntimeModel";
            }
            if (!service.Provisioned)
            {
                return "Unprovisioned:AdapterNotProvisioned";
            }
            if (!hasCredential)
            {
                return "Unprovisioned:MissingCredential";
            }
            string endpoint;
            if (!disclosedEndpoints.TryGetValue((service.Service, purpose), out endpoint)
                || !string.Equals(endpoint, service.Endpoint, StringComparison.Ordinal))
            {
                if (!disclosureGiven) return "Denied:FirstSendDisclosureMissing";
                RecordFirstSendDisclosure(service, purpose);
            }
            if (isDecision && CountDecisions() >= GatewayLimits.MaxConcurrentPerDecisionPurpose)
            {
                return "RateLimited:DecisionPurpose";
            }
            if (pending.Count >= GatewayLimits.MaxConcurrentGameWide)
            {
                return "RateLimited:GameWide";
            }
            return null;
        }

        private static string DisclosureLabel(GatewayPurpose purpose)
        {
            switch (purpose)
            {
                case GatewayPurpose.ResidentDecision:
                    return "needs,available-activity-ids,known-history-event-ids,constraints";
                default:
                    return "purpose:" + purpose.ToString() + ":adapter-owned-minimal-payload";
            }
        }

        // BP-AI-14 (owner amendment 2026-11-04): Coding-assistant models,
        // Muse Wonder and every Spark id except muse-spark-1.3 have no runtime
        // role and are refused, never sent. muse-spark-1.3 alone runs voice
        // interpretation and resident decisions (d1-ultra is not served).
        private static bool IsProhibitedRuntimeModel(string modelId)
        {
            if (string.IsNullOrEmpty(modelId)
                || string.Equals(modelId, "muse-spark-1.3", StringComparison.Ordinal))
            {
                return false;
            }
            return modelId.IndexOf("spark", StringComparison.OrdinalIgnoreCase) >= 0
                || modelId.IndexOf("muse-code", StringComparison.OrdinalIgnoreCase) >= 0
                || modelId.IndexOf("muse-wonder", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Typed parsing (BP-AI-01): a decision choice is a non-empty ACT code
        // with no whitespace. Unknown-but-wellformed codes pass parsing and
        // fail simulation validation instead, which names its own reason.
        private static string ParseError(string activityCode)
        {
            if (string.IsNullOrEmpty(activityCode))
            {
                return "Parse:EmptyActivityCode";
            }
            for (int i = 0; i < activityCode.Length; i++)
            {
                if (char.IsWhiteSpace(activityCode[i]))
                {
                    return "Parse:BadActivityCode";
                }
            }
            return null;
        }

        // BP-AI-02 freshness: the stamped context version must still match
        // current state. Returns the first mismatched field, or null.
        private string FreshnessMismatch(PendingEntry entry)
        {
            FreshnessStamp stamped = entry.Request.ContextVersion;
            FreshnessStamp current = entry.Request.Resident.HasValue
                ? arbitrator.CurrentStamp(entry.Request.Resident.Value)
                : new FreshnessStamp(world.Epoch, arbitrator.WorldSequence, 0,
                    default(HomeId), 0u, 0u);
            if (stamped.Epoch != current.Epoch)
            {
                return "Stale:Epoch";
            }
            if (stamped.Sequence != current.Sequence)
            {
                return "Stale:Sequence";
            }
            if (stamped.QueueLength != current.QueueLength)
            {
                return "Stale:QueueLength";
            }
            if (stamped.Location != current.Location)
            {
                return "Stale:Location";
            }
            if (stamped.DecisionSlotGeneration != current.DecisionSlotGeneration)
            {
                return "Stale:DecisionSlot";
            }
            if (stamped.ControlGeneration != current.ControlGeneration)
            {
                return "Stale:Control";
            }
            return null;
        }

        private int CountDecisions()
        {
            int count = 0;
            foreach (PendingEntry entry in pending.Values)
            {
                if (entry.IsDecision)
                {
                    count++;
                }
            }
            return count;
        }

        private void AppendLog(long requestId, string modelId, GatewayPurpose purpose,
            GatewayOutcome outcome, string detail)
        {
            ProposalLogEntry entry = new ProposalLogEntry(nextSequence, world.GameSeconds,
                requestId, modelId, purpose, outcome, detail);
            nextSequence++;
            if (log.Count >= GatewayLimits.DecisionLogMaxRecords)
            {
                log.RemoveAt(0);
                truncatedRecords++;
            }
            log.Add(entry);
        }

        private static int CompareRequestIds(GatewayRequestId a, GatewayRequestId b)
        {
            return ((long)a).CompareTo((long)b);
        }
    }
}
