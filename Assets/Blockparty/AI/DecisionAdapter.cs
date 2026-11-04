// D1U@1: the bounded resident-decision adapter (CONTRACT-BP-04-D1U).
// Consumes AI-BOUNDARY@1 (ProposalBoundary issuance, completion, deadlines,
// resume) and PROBE-D1U@1 (documented endpoint/model record) over one
// injected transport. The adapter owns no wire format, no clock and no
// fallback of its own: RequestDecision delegates issuance, refusal and
// payload minimization to the boundary and only issued requests reach Send;
// Tick advances the boundary's real-time deadline, drains transport replies
// and drives boundary Tick/OnResume. Applied replies are candidates only and
// land through CompleteDecision; every other reply outcome lands through
// CompleteFailure, which runs the real local fallback. An empty transport
// means still pending, never fabricated success. Deferred access
// (ServiceEndpoint.Provisioned=false) refuses inside the boundary with an
// immediate local fallback and no Send. Plain C# 9, no engine dependency,
// no network, no credential material: the transport takes no secret and the
// adapter performs no lookup, storage or logging of any. META-LIVE: the two
// static helpers below hold the /v1/responses body and reply shape so the
// live transport and the live probe send exactly the same request.
// Fast Enter Play Mode: no static mutable state, explicit numeric
// conversions, no AppDomain, Assembly.Location, BinaryFormatter,
// wall-clock reads or String.GetHashCode.
using System;
using System.Globalization;
using System.Text;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;

namespace Blockparty.AI
{
    // Typed gateway seam for one decision provider. The fake in the D1U
    // suite and the future live transport both implement this; Liquid wire
    // JSON stays behind the implementation, never in this package.
    public interface IDecisionTransport
    {
        void Send(GatewayRequest request, DecisionPayload payload);
        bool TryReceive(out DecisionResponse response);
    }

    // One typed provider reply: which request it answers, the outcome class,
    // and the candidate choice for Applied replies. Choice fields are
    // advisory until the boundary validates them against the offered set.
    public sealed class DecisionResponse
    {
        public readonly GatewayRequestId RequestId;
        public readonly GatewayOutcome Outcome;
        public readonly string ActivityCode;
        public readonly long? TargetId;

        public DecisionResponse(GatewayRequestId requestId, GatewayOutcome outcome,
            string activityCode, long? targetId)
        {
            RequestId = requestId;
            Outcome = outcome;
            ActivityCode = activityCode;
            TargetId = targetId;
        }
    }

    public sealed class DecisionAdapter
    {
        private readonly ProposalBoundary boundary;
        private readonly IDecisionTransport transport;

        public DecisionAdapter(ProposalBoundary boundary, IDecisionTransport transport)
        {
            if (boundary == null)
            {
                throw new ArgumentNullException("boundary");
            }
            if (transport == null)
            {
                throw new ArgumentNullException("transport");
            }
            this.boundary = boundary;
            this.transport = transport;
        }

        public GatewayRequest RequestDecision(ResidentId resident, ServiceEndpoint service,
            bool hasCredential, bool disclosureGiven)
        {
            GatewayRequest request = boundary.RequestDecision(
                resident, service, hasCredential, disclosureGiven);
            if (request == null)
            {
                return null;
            }
            DecisionPayload payload = boundary.BuildDecisionPayload(resident);
            try
            {
                transport.Send(request, payload);
            }
            catch (Exception)
            {
                // An unavailable transport completes the issued request as a
                // recorded terminal failure so the local fallback still runs.
                boundary.CompleteFailure(request.Id, GatewayOutcome.Malformed);
            }
            return request;
        }

        // META-LIVE (owner decision 2026-11-04): the resident decision goes to muse-spark-1.3 at
        // medium effort in the endpoint's Responses shape (the D1U "questions" body is a 400 there):
        // instructions plus the minimized payload as JSON text; the reply is one offered id.
        public static string ResponsesBody(string modelId, DecisionPayload payload)
        {
            JsonObject state = new JsonObject();
            JsonObject needs = new JsonObject();   // labelled by Need: an unlabelled array made the model deliberate past 10 s
            JsonArray ids = new JsonArray();
            JsonArray history = new JsonArray();
            if (payload != null)
            {
                for (int i = 0; i < payload.Needs.Length; i++)
                    needs.Add(((Need)i).ToString(), new JsonNumber(payload.Needs[i].ToString("R", CultureInfo.InvariantCulture)));
                foreach (string id in payload.AvailableActivityIds) ids.Add(new JsonString(id ?? string.Empty));
                foreach (long e in payload.KnownHistoryEventIds) history.Add(new JsonNumber(e.ToString(CultureInfo.InvariantCulture)));
            }
            state.Add("needs", needs);
            state.Add("available_activity_ids", ids);
            state.Add("known_history_event_ids", history);
            state.Add("constraints", payload == null ? string.Empty : payload.Constraints);
            StringBuilder input = new StringBuilder();
            state.Render(input);
            JsonObject reasoning = new JsonObject();
            reasoning.Add("effort", "medium");
            JsonObject root = new JsonObject();
            root.Add("model", modelId);
            root.Add("reasoning", reasoning);
            root.Add("instructions", "You choose the next activity for one resident of a life-simulation game. needs are "
                + "0-100, lower is more urgent. Needs served (Autonomy gain table): FOOD Hunger; HEALTH-01, HEALTH-02 "
                + "Energy; HEALTH-03 Hygiene; HEALTH-04 Bladder; REL, CLUB, PARTY Social; SPORT-02, SPORT-03, CREA, PARTY Fun. "
                + "Decide quickly. Reply with exactly one of available_activity_ids, copied verbatim, that best "
                + "addresses the resident's lowest unmet need while respecting constraints. No other text.");
            root.Add("input", input.ToString());
            StringBuilder text = new StringBuilder();
            root.Render(text);
            return text.ToString();
        }

        // A Responses reply's first output_text, re-expressed as the D1U answers.activity.choice so
        // LiveWire.ParseDecision validates it unchanged; any other body passes through as is.
        public static string AnswersFromResponses(string body)
        {
            try
            {
                JsonObject root = JsonParser.ParseSamFrame(body) as JsonObject;
                if (root == null || !root.Has("output")) return body;
                JsonArray output = root.GetArray("output", "decision");
                for (int i = 0; i < output.Count; i++)
                {
                    JsonObject item = output.GetObject(i, "decision.output");
                    if (item.GetString("type", "decision.output") != "message") continue;
                    JsonArray parts = item.GetArray("content", "decision.output.message");
                    for (int c = 0; c < parts.Count; c++)
                    {
                        JsonObject part = parts.GetObject(c, "decision.output.message.content");
                        if (part.GetString("type", "decision.output.message.content") != "output_text") continue;
                        JsonObject activity = new JsonObject();
                        activity.Add("choice", part.GetString("text", "decision.output.message.content").Trim('\'', '`', ' ', '\n'));
                        JsonObject answers = new JsonObject();
                        answers.Add("activity", activity);
                        JsonObject wrapped = new JsonObject();
                        wrapped.Add("answers", answers);
                        StringBuilder text = new StringBuilder();
                        wrapped.Render(text);
                        return text.ToString();
                    }
                }
            }
            catch (Exception)
            {
            }
            return body;
        }

        public void Tick(double elapsedRealSeconds)
        {
            boundary.AdvanceRealSeconds(elapsedRealSeconds);
            try
            {
                DecisionResponse response;
                while (transport.TryReceive(out response))
                {
                    if (response == null)
                    {
                        break;
                    }
                    if (response.Outcome == GatewayOutcome.Applied)
                    {
                        boundary.CompleteDecision(response.RequestId,
                            response.ActivityCode, response.TargetId);
                    }
                    else
                    {
                        // CompleteFailure accepts only the terminal failure
                        // outcomes; any other reply is invalid and completes as
                        // Malformed so Tick never throws and still runs the
                        // boundary Tick/OnResume below.
                        GatewayOutcome outcome = response.Outcome;
                        if (outcome != GatewayOutcome.Denied && outcome != GatewayOutcome.Unprovisioned
                            && outcome != GatewayOutcome.Timeout && outcome != GatewayOutcome.Malformed
                            && outcome != GatewayOutcome.Cancelled)
                        {
                            outcome = GatewayOutcome.Malformed;
                        }
                        boundary.CompleteFailure(response.RequestId, outcome);
                    }
                }
            }
            catch (Exception)
            {
                // A failed receive ends the drain; the boundary Tick/OnResume
                // below still run so deadlines and the local fallback proceed.
            }
            boundary.Tick();
            boundary.OnResume();
        }
    }
}
