// CREDENTIAL-DESIGN production checks. Standalone console program: compiled and
// run with the Unity-bundled Roslyn + netcorerun until TOOLCHAIN lands the
// Unity test path. Proves the OD-3 (2026-10-27) design against the
// AI-BOUNDARY@1 code: each player's own locally stored key, no shipped or
// logged credential, no hosted backend, and preserved no-key local fallbacks.
// Positive checks prove keyless requests refuse with Unprovisioned plus
// MissingCredential while manual play and the Autonomy fallback stay
// available, the keyed path still issues, and the decision log carries no
// credential or endpoint text. Negative checks prove a shipped key has no API
// slot, agent-style ambient credential access cannot reach the boundary (the
// presence boolean is the only signal), and a missing key can never yield a
// connected success. No network and no credential material: the only
// key-shaped text in this file is the inert scan literals below, which carry
// no values. Mirrors the AI-BOUNDARY/COMMAND/CLOCK/STATE pattern.
using System;
using System.Collections.Generic;
using System.Reflection;
using Blockparty.AI;
using Blockparty.Contracts;
using Blockparty.Simulation;

public static class CredentialDesignChecks
{
    private const StateWriter Core = StateWriter.SimCore;

    private static int failures;
    private static long nextId = 71000;

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

    private static ServiceEndpoint VoiceSvc()
    {
        return new ServiceEndpoint("meta-voice", "wss://api.meta.ai/v1/asr/realtime", "muse-voice-1", true);
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

    private static bool LogHasOutcome(Stack stack, GatewayOutcome outcome)
    {
        IReadOnlyList<ProposalLogEntry> log = stack.Boundary.DecisionLog;
        for (int i = 0; i < log.Count; i++)
        {
            if (log[i].Outcome == outcome)
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

    // A member or parameter name that would carry a credential value. The one
    // allowed exception is the hasCredential presence boolean itself, checked
    // by type at the call site. KnownHistoryEventIds holds event ids, not
    // key material: it contains neither "Key" nor a whole-word "key".
    private static bool IsCredentialish(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }
        if (name.IndexOf("Key", StringComparison.Ordinal) >= 0
            && !string.Equals(name, "KnownHistoryEventIds", StringComparison.Ordinal))
        {
            return true;
        }
        return name.IndexOf("apikey", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("api_key", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("bearer", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("passwd", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("authorization", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("credential", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool PublicSurfaceClean(Type type, out string witness)
    {
        witness = string.Empty;
        FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
        for (int i = 0; i < fields.Length; i++)
        {
            if (IsCredentialish(fields[i].Name))
            {
                witness = type.Name + "." + fields[i].Name;
                return false;
            }
        }
        PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        for (int i = 0; i < properties.Length; i++)
        {
            if (IsCredentialish(properties[i].Name))
            {
                witness = type.Name + "." + properties[i].Name;
                return false;
            }
        }
        return true;
    }

    public static int Main()
    {
        // Keyless decision requests refuse with Unprovisioned plus
        // MissingCredential, create nothing pending, and run the local
        // fallback for a live resident under player automation.
        {
            Stack stack = NewStack(101L);
            ResidentRecord resident = AddResident(stack.World, "Ada");
            Check(stack.Boundary.RequestDecision(resident.Id, D1U(true), false, true) == null, "Keyless.DecisionRefused");
            Check(LogHas(stack, GatewayOutcome.Unprovisioned, "Unprovisioned:MissingCredential"), "Keyless.DecisionLogged");
            Check(stack.Boundary.PendingCount == 0, "Keyless.NothingPending");
            Check(LogHas(stack, GatewayOutcome.Unprovisioned, "Fallback:"), "Keyless.FallbackRuns");
            Check(stack.Autonomy.Decisions.Count == 1, "Keyless.AutonomyDecided");
            Check(CountOwner(stack, resident.Id, ActionOwner.Autonomous) == 1, "Keyless.FallbackEnqueued");
        }

        // Keyless adapter requests (voice endpoint here) refuse the same way;
        // the caller takes its defined manual alternative.
        {
            Stack stack = NewStack(102L);
            Check(stack.Boundary.RequestService(null, GatewayPurpose.VoiceTranscription, VoiceSvc(), false, true) == null, "Keyless.ServiceRefused");
            Check(LogHas(stack, GatewayOutcome.Unprovisioned, "Unprovisioned:MissingCredential"), "Keyless.ServiceLogged");
            Check(stack.Boundary.PendingCount == 0, "Keyless.ServiceNothingPending");
        }

        // A missing key can never yield a connected success: nothing is
        // pending, so no completion path runs and no Applied outcome lands.
        {
            Stack stack = NewStack(103L);
            ResidentRecord resident = AddResident(stack.World, "Bo");
            GatewayRequest decision = stack.Boundary.RequestDecision(resident.Id, D1U(true), false, true);
            GatewayRequest service = stack.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, VoiceSvc(), false, true);
            Check(decision == null && service == null, "NoSuccess.BothRefused");
            Check(stack.Boundary.Tick() == 0, "NoSuccess.TickIdle");
            Check(!LogHasOutcome(stack, GatewayOutcome.Applied), "NoSuccess.NoAppliedClaim");
        }

        // Local play stays fully available without a key: manual commands
        // land, the fallback decision is already queued, and a second
        // resident plays on untouched.
        {
            Stack stack = NewStack(104L);
            ResidentRecord resident = AddResident(stack.World, "Cy");
            stack.Boundary.RequestDecision(resident.Id, D1U(true), false, true);
            Check(CountOwner(stack, resident.Id, ActionOwner.Autonomous) == 1, "LocalPlay.FallbackPresent");
            Check(Direct(stack, resident, "ACT-FOOD-02", QueueMode.Immediate).Accepted, "LocalPlay.DirectAccepted");
            ResidentRecord other = AddResident(stack.World, "Dee");
            Check(Direct(stack, other, "ACT-FOOD-02", QueueMode.Immediate).Accepted, "LocalPlay.SecondResident");
            Check(stack.Boundary.RequestDecision(other.Id, D1U(true), false, true) == null, "LocalPlay.StillKeyless");
        }

        // The keyed path still issues and applies: the design keeps the
        // connected route for key holders next to the local fallback.
        {
            Stack stack = NewStack(105L);
            ResidentRecord resident = AddResident(stack.World, "Eli");
            GatewayRequest request = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            Check(request != null, "Keyed.Issued");
            string code = stack.Arbitrator.Snapshot(resident.Id).Available[0].Activity.Code;
            Check(stack.Boundary.CompleteDecision(request.Id, code, null) == ProposalVerdict.Applied, "Keyed.Applied");
            Check(LogHas(stack, GatewayOutcome.Applied, code), "Keyed.Logged");
        }

        // Provisioning is config, not a key: even with a credential present,
        // an unprovisioned adapter refuses and never sends.
        {
            Stack stack = NewStack(106L);
            ResidentRecord resident = AddResident(stack.World, "Fay");
            Check(stack.Boundary.RequestDecision(resident.Id, D1U(false), true, true) == null, "Provision.Refused");
            Check(LogHas(stack, GatewayOutcome.Unprovisioned, "Unprovisioned:AdapterNotProvisioned"), "Provision.Logged");
            Check(stack.Boundary.PendingCount == 0, "Provision.NothingPending");
        }

        // The decision log never carries key material or endpoint text across
        // success, refusal, malformed, and timeout paths.
        {
            Stack stack = NewStack(107L);
            ResidentRecord resident = AddResident(stack.World, "Gus");
            GatewayRequest good = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            stack.Boundary.CompleteDecision(good.Id, stack.Arbitrator.Snapshot(resident.Id).Available[0].Activity.Code, null);
            stack.Boundary.RequestDecision(resident.Id, D1U(true), false, true);
            GatewayRequest bad = stack.Boundary.RequestDecision(resident.Id, D1U(true), true, true);
            stack.Boundary.CompleteDecision(bad.Id, string.Empty, null);
            stack.Boundary.RequestService(null, GatewayPurpose.VoiceTranscription, VoiceSvc(), true, true);
            stack.Boundary.AdvanceRealSeconds(10.0);
            stack.Boundary.Tick();
            bool clean = true;
            IReadOnlyList<ProposalLogEntry> log = stack.Boundary.DecisionLog;
            for (int i = 0; i < log.Count; i++)
            {
                string both = log[i].ModelId + "|" + log[i].Detail;
                if (both.IndexOf("https://", StringComparison.Ordinal) >= 0
                    || both.IndexOf("wss://", StringComparison.Ordinal) >= 0
                    || both.IndexOf("Bearer", StringComparison.Ordinal) >= 0
                    || both.IndexOf("MODEL_API_KEY", StringComparison.Ordinal) >= 0
                    || both.IndexOf("Authorization", StringComparison.Ordinal) >= 0
                    || both.IndexOf("sk-", StringComparison.Ordinal) >= 0)
                {
                    clean = false;
                }
            }
            Check(log.Count >= 4, "Secrets.DrovePaths");
            Check(clean, "Secrets.NoneLogged");
        }

        // The endpoint config has no key slot: exactly three config strings
        // plus the provisioned flag, and no key-shaped member.
        {
            FieldInfo[] fields = typeof(ServiceEndpoint).GetFields(BindingFlags.Public | BindingFlags.Instance);
            int strings = 0;
            int flags = 0;
            bool namesClean = true;
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].FieldType == typeof(string))
                {
                    strings++;
                }
                if (fields[i].FieldType == typeof(bool))
                {
                    flags++;
                }
                if (IsCredentialish(fields[i].Name))
                {
                    namesClean = false;
                }
            }
            Check(fields.Length == 4, "Endpoint.FourFields");
            Check(strings == 3, "Endpoint.ThreeStrings");
            Check(flags == 1, "Endpoint.OneFlag");
            Check(namesClean, "Endpoint.NoKeySlot");
            Check(typeof(ServiceEndpoint).GetProperties(BindingFlags.Public | BindingFlags.Instance).Length == 0, "Endpoint.NoProperties");
            ConstructorInfo[] ctors = typeof(ServiceEndpoint).GetConstructors();
            Check(ctors.Length == 1, "Endpoint.OneCtor");
            ParameterInfo[] endpointParams = ctors[0].GetParameters();
            Check(endpointParams.Length == 4, "Endpoint.CtorTakesFour");
            bool ctorClean = true;
            for (int i = 0; i < endpointParams.Length; i++)
            {
                if (IsCredentialish(endpointParams[i].Name))
                {
                    ctorClean = false;
                }
            }
            Check(ctorClean, "Endpoint.CtorTakesNoKey");
        }

        // The boundary public surface takes no key: the only credential-named
        // parameter is the hasCredential presence boolean.
        {
            bool surfaceClean = true;
            string witness = string.Empty;
            MethodInfo[] methods = typeof(ProposalBoundary).GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            for (int i = 0; i < methods.Length; i++)
            {
                if (IsCredentialish(methods[i].Name))
                {
                    surfaceClean = false;
                    witness = methods[i].Name;
                }
                ParameterInfo[] methodParams = methods[i].GetParameters();
                for (int j = 0; j < methodParams.Length; j++)
                {
                    if (string.Equals(methodParams[j].Name, "hasCredential", StringComparison.Ordinal))
                    {
                        if (methodParams[j].ParameterType != typeof(bool))
                        {
                            surfaceClean = false;
                            witness = methods[i].Name + ".hasCredential";
                        }
                    }
                    else if (IsCredentialish(methodParams[j].Name))
                    {
                        surfaceClean = false;
                        witness = methods[i].Name + "." + methodParams[j].Name;
                    }
                }
            }
            ConstructorInfo[] ctors = typeof(ProposalBoundary).GetConstructors();
            for (int i = 0; i < ctors.Length; i++)
            {
                ParameterInfo[] ctorParams = ctors[i].GetParameters();
                for (int j = 0; j < ctorParams.Length; j++)
                {
                    if (IsCredentialish(ctorParams[j].Name))
                    {
                        surfaceClean = false;
                        witness = "ctor." + ctorParams[j].Name;
                    }
                }
            }
            if (!PublicSurfaceClean(typeof(ProposalBoundary), out string fieldWitness))
            {
                surfaceClean = false;
                witness = fieldWitness;
            }
            Check(surfaceClean, "Api.BoundaryTakesNoKey");
            Check(witness.Length == 0, "Api.NoWitness");
        }

        // Requests, log entries, and payloads carry no credential field: there
        // is nowhere for a shipped or logged key to sit.
        {
            Check(PublicSurfaceClean(typeof(GatewayRequest), out string requestWitness) && requestWitness.Length == 0, "Api.RequestCarriesNoKey");
            Check(PublicSurfaceClean(typeof(ProposalLogEntry), out string logWitness) && logWitness.Length == 0, "Api.LogCarriesNoKey");
            Check(PublicSurfaceClean(typeof(DecisionPayload), out string payloadWitness) && payloadWitness.Length == 0, "Api.PayloadCarriesNoKey");
            Check(typeof(DecisionPayload).GetFields(BindingFlags.Public | BindingFlags.Instance).Length == 4, "Api.PayloadHasFour");
        }

        // No ambient credential access: the presence boolean is the only
        // signal, so repeat keyless calls refuse identically and the
        // boundary learns nothing between them.
        {
            Stack stack = NewStack(108L);
            ResidentRecord resident = AddResident(stack.World, "Hal");
            Check(stack.Boundary.RequestDecision(resident.Id, D1U(true), false, true) == null, "Ambient.FirstRefused");
            Check(stack.Boundary.RequestDecision(resident.Id, D1U(true), false, true) == null, "Ambient.SecondRefused");
            Check(stack.Boundary.RequestService(null, GatewayPurpose.ImageGeneration,
                new ServiceEndpoint("meta-image", "https://api.meta.ai/v1", "muse-image-1", true), false, true) == null, "Ambient.ServiceRefused");
            Check(stack.Boundary.PendingCount == 0, "Ambient.NothingPending");
            Check(!LogHasOutcome(stack, GatewayOutcome.Applied), "Ambient.NoAppliedClaim");
        }

        // Repeated play entry is deterministic: a fresh boundary starts empty
        // and the same keyless flow logs the same details.
        {
            Stack first = NewStack(109L);
            ResidentRecord a = AddResident(first.World, "Ivy");
            Check(first.Boundary.PendingCount == 0 && first.Boundary.DecisionLog.Count == 0, "Determinism.StartsEmpty");
            first.Boundary.RequestDecision(a.Id, D1U(true), false, true);
            first.Boundary.RequestService(null, GatewayPurpose.VoiceTranscription, VoiceSvc(), false, true);
            Stack second = NewStack(109L);
            ResidentRecord b = AddResident(second.World, "Ivy");
            second.Boundary.RequestDecision(b.Id, D1U(true), false, true);
            second.Boundary.RequestService(null, GatewayPurpose.VoiceTranscription, VoiceSvc(), false, true);
            bool same = first.Boundary.DecisionLog.Count == second.Boundary.DecisionLog.Count
                && first.Boundary.DecisionLog.Count == 2;
            for (int i = 0; i < first.Boundary.DecisionLog.Count && same; i++)
            {
                if (!string.Equals(first.Boundary.DecisionLog[i].Detail, second.Boundary.DecisionLog[i].Detail, StringComparison.Ordinal)
                    || first.Boundary.DecisionLog[i].Outcome != second.Boundary.DecisionLog[i].Outcome)
                {
                    same = false;
                }
            }
            Check(same, "Determinism.SameKeylessLog");
        }

        if (failures == 0)
        {
            Console.WriteLine("CREDENTIAL-DESIGN ALL PASS");
        }
        return failures;
    }
}
