/*
VOICE@1 test-first contract. Resource: isolated-csharp; no editor/display/network.
Sources: plan.json VOICE, m3/test-engineer.json suite_checks, BP-AI-08/09/10,
BP-SEC-07 and the existing VoiceMeaning/VoiceTranscript command contract.
Fixtures below are synthetic text in PROBE-VOICE's qualified final-transcript
shape, plus its recorded ErrorEvent shape. No credential or microphone I/O.
A separate module is required because VOICE has its own package gate/sentinel.
Reach: only this suite and red.log; no callers, config, exports or product edits.

Public adapter seam for the product step (reflection follows SELECT-ACTION):
 VoiceAdapter(WorldState, ProposalBoundary, CommandArbitrator)
 GatewayRequest Request(ServiceEndpoint, bool hasCredential,
                        bool disclosureGiven, bool microphoneGranted)
 ProposalVerdict Complete(GatewayRequestId, string frame)
 VoiceTranscript Transcript; string Meaning, Prompt;
 CommandEnvelope LastCommand; CommandResult? LastResult;
 IReadOnlyList<CommandEnvelope> Candidates;
 CommandResult Choose(int candidateIndex); void Dismiss();
Named resident/activity/destination resolution uses the current world/catalog.
Canonical phrases below fix the local mapping inputs, not ASR recognition.
Complete may finish a supported command while paused (notably resume); the
one normal arbitrator remains the command authority. No durable adapter state
is required: this suite introduces no persistence/schema-version contract.

Mapping (local evidence only, NOT completion of the full SEIT procedures):
 SupportedPairs (10 meanings, plus destination) -> SEIT-AI-08-P2,
 SEIT-IF-VOICE-P1; local support for SEIT-AI-08-P1, SEIT-AC-16, SEIT-SUB-01.
 UnavailableActivity -> SEIT-AI-08-N2, SEIT-IF-VOICE-N4.
 InertText: out-of-set -> SEIT-AI-08-N1; empty -> SEIT-AI-09-N2,
 SEIT-IF-VOICE-N2; nonsense -> SEIT-AI-09-N3, SEIT-IF-VOICE-N3.
 AmbiguityChoice/Dismiss -> SEIT-AI-09-P1/N1, SEIT-IF-VOICE-P2/N1.
 DeniedFrame, RefusedRequestManual -> SEIT-AI-10-P1/N1,
 SEIT-IF-VOICE-N5, SEIT-TB-04-N1 (command/model layer only).
 DeniedObservationOracle -> required negative fixture: corrupt the OBSERVED
 denied outcome to Applied, then run the identical no-connected-success oracle.
 Live service, rendered meanings, OS permission/egress, all ten keyboard/mouse
 flows, recordings and submission inspection remain pending at their own gates.
*/
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Blockparty.AI;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;
using Blockparty.UI;

public static class VoiceChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private static int failures;
    private static bool negativeFixture;
    // RECORDED ErrorEvent shape: tests/production/PROBE-VOICE.cs (Meta docs).
    private const string DeniedFrame = "{\"type\":\"error\",\"message\":\"Permission denied\",\"sessionId\":\"550e8400-e29b-41d4-a716-446655440000\"}";
    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { IncludeFields = true };

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
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
    private static object Call(object instance, string name, params object[] args)
    {
        MethodInfo method = instance.GetType().GetMethod(name);
        Check(method != null, "missing VOICE capability VoiceAdapter." + name);
        return method.Invoke(instance, args);
    }
    private static T Read<T>(object instance, string name)
    {
        PropertyInfo property = instance.GetType().GetProperty(name);
        if (property != null) return (T)property.GetValue(instance);
        FieldInfo field = instance.GetType().GetField(name);
        Check(field != null, "missing VOICE capability VoiceAdapter." + name);
        return (T)field.GetValue(instance);
    }
    private static ServiceEndpoint Endpoint(bool provisioned = true)
    {
        return new ServiceEndpoint("meta-voice", "wss://api.meta.ai/v1/asr/realtime",
            "muse-voice-transcribe-1.0", provisioned);
    }
    private static string Frame(string text)
    {
        // SYNTHETIC, using PROBE-VOICE FinalTranscript's qualified shape.
        return "{\"type\":\"transcript\",\"transcript\":" + JsonSerializer.Serialize(text)
            + ",\"final\":true,\"audioProcessedMs\":3600}";
    }

    private sealed class Fixture
    {
        public readonly WorldState World = WorldState.CreateSeeded(7L, 4242L, 0, null);
        public readonly ResidentRecord Alex, Sam, Samantha;
        public readonly Clock Clock;
        public readonly CommandArbitrator Commands;
        public readonly ProposalBoundary Boundary;
        public readonly ActivityExecutor Executor;
        public readonly BlockSites Sites;
        public readonly BlockSite Work;
        public readonly WorldNavigation Nav;
        public readonly Accessibility Access = new Accessibility(new SettingsRecord(1, 1f, 1f, false));
        public Fixture()
        {
            HomeRecord home = World.AllocateHome(Core, 8);
            HouseholdRecord house = World.AllocateHousehold(Core, "Residents", home.Id);
            Alex = World.AllocateResident(Core, "Alex", LifeStage.Adult, house.Id, home.Id);
            Sam = World.AllocateResident(Core, "Sam", LifeStage.Adult, house.Id, home.Id);
            Samantha = World.AllocateResident(Core, "Samantha", LifeStage.Adult, house.Id, home.Id);
            Clock = new Clock(World);
            Commands = new CommandArbitrator(World, Clock);
            Executor = new ActivityExecutor(World, Commands, Clock);
            Boundary = new ProposalBoundary(World, Clock, Commands, new Autonomy(World, Commands));
            Sites = new BlockSites(World, Executor);
            foreach (BlockSite site in Sites.Sites) if (site.Id == "office") Work = site;
            Check(Work != null, "real BLOCK-SITES named office destination");
            Nav = new WorldNavigation(World, Commands, Executor, Sites);
        }
        public object Adapter()
        {
            Type type = typeof(ProposalBoundary).Assembly.GetType("Blockparty.AI.VoiceAdapter");
            Check(type != null, "missing VOICE capability Blockparty.AI.VoiceAdapter");
            return Activator.CreateInstance(type, World, Boundary, Commands, Sites, Executor);
        }
        public GatewayRequest Request(object adapter)
        {
            GatewayRequest request = (GatewayRequest)Call(adapter, "Request", Endpoint(), true, true, true);
            Check(request != null && request.Purpose == GatewayPurpose.VoiceTranscription,
                "typed voice request required");
            return request;
        }
        public CommandEnvelope Envelope(CommandKind kind, CommandSource source,
            ResidentId? resident = null, string activity = null, long? target = null)
        {
            ResidentRecord record = null;
            if (resident.HasValue) World.TryGetResident(resident.Value, out record);
            CommandParameters parameters = CommandParameters.None.Instance;
            if (kind == CommandKind.DirectActivity) parameters = new CommandParameters.Direct(QueueMode.Immediate);
            if (kind == CommandKind.TravelTo) parameters = new CommandParameters.Travel(QueueMode.Immediate, TravelTargetKind.Object);
            if (kind == CommandKind.SetSpeed) parameters = new CommandParameters.Speed(SimulationSpeed.Fast);
            return new CommandEnvelope(World.AllocateCommand(Core), World.Epoch, World.GameSeconds,
                source, record == null ? Commands.WorldSequence + 1u : record.ResidentSequence + 1u,
                kind, resident, activity == null ? (ActivityId?)null : new ActivityId(activity), target, parameters);
        }
        public CommandResult Direct(string code)
        {
            return Commands.Submit(Envelope(CommandKind.DirectActivity, CommandSource.PlayerHuman, Alex.Id, code));
        }
    }

    private sealed class Phrase
    {
        public readonly string Text;
        public readonly VoiceMeaning Meaning;
        public readonly CommandKind Kind;
        public readonly bool Resident;
        public readonly string Activity;
        public Phrase(string text, VoiceMeaning meaning, CommandKind kind, bool resident = false, string activity = null)
        { Text = text; Meaning = meaning; Kind = kind; Resident = resident; Activity = activity; }
    }
    private static readonly Phrase[] Phrases = {
        new Phrase("select Alex", VoiceMeaning.SelectResident, CommandKind.SelectResident, true),
        new Phrase("follow Alex", VoiceMeaning.FollowResident, CommandKind.FollowResident, true),
        new Phrase("stop following", VoiceMeaning.StopFollowing, CommandKind.StopFollowing),
        new Phrase("direct Alex to study", VoiceMeaning.DirectResident, CommandKind.DirectActivity, true, "ACT-EDU-02"),
        new Phrase("pause", VoiceMeaning.Pause, CommandKind.Pause),
        new Phrase("resume", VoiceMeaning.Resume, CommandKind.Resume),
        new Phrase("set speed fast", VoiceMeaning.SetSpeed, CommandKind.SetSpeed),
        new Phrase("cancel Alex current action", VoiceMeaning.CancelCurrent, CommandKind.CancelCurrent, true),
        new Phrase("open map", VoiceMeaning.OpenMap, CommandKind.OpenMap),
        new Phrase("save game", VoiceMeaning.SaveGame, CommandKind.SaveGame),
        new Phrase("direct Alex to office", VoiceMeaning.DirectResident, CommandKind.TravelTo, true)
    };
    private static void Prepare(Fixture f, Phrase phrase)
    {
        if (phrase.Kind == CommandKind.CancelCurrent) Check(f.Direct("ACT-EDU-02").Accepted, "real cancellable action");
        if (phrase.Kind == CommandKind.Resume) f.Clock.Pause();
    }
    private static CommandEnvelope Expected(Fixture f, Phrase phrase)
    {
        return f.Envelope(phrase.Kind, CommandSource.PlayerHuman,
            phrase.Resident ? f.Alex.Id : (ResidentId?)null, phrase.Activity,
            phrase.Kind == CommandKind.TravelTo ? (long)f.Work.Object : (long?)null);
    }
    private static string State(Fixture f)
    {
        WorldSnapshot snapshot = f.World.ExportSnapshot();
        // Only the diagnostic gateway request allocation differs from no-input control.
        snapshot.NextGatewayId = 0;
        return JsonSerializer.Serialize(snapshot, Json) + "|" + f.Clock.Speed + "|" + f.Clock.Paused;
    }
    private static void SameCommand(CommandEnvelope actual, CommandEnvelope expected)
    {
        Check(actual != null && actual.Source == CommandSource.VoiceMapped, "Source=VoiceMapped ordinary envelope");
        Check(actual.Kind == expected.Kind && actual.Resident == expected.Resident
            && actual.Activity?.Code == expected.Activity?.Code && actual.TargetId == expected.TargetId,
            "Kind/resident/activity/target equal manual oracle");
        Check(JsonSerializer.Serialize(actual.Parameters, actual.Parameters.GetType(), Json)
            == JsonSerializer.Serialize(expected.Parameters, expected.Parameters.GetType(), Json),
            "typed parameters equal manual oracle");
    }
    private static void SupportedPair(Phrase phrase)
    {
        var voice = new Fixture(); var manual = new Fixture();
        Prepare(voice, phrase); Prepare(manual, phrase);
        object adapter = voice.Adapter();
        GatewayRequest request = voice.Request(adapter);
        int before = voice.Commands.CommandLog.Count;
        Call(adapter, "Complete", request.Id, Frame(phrase.Text));
        VoiceTranscript transcript = Read<VoiceTranscript>(adapter, "Transcript");
        Check(transcript.Text == phrase.Text && transcript.Meaning == phrase.Meaning,
            "recognized text and supported meaning");
        Check(!string.IsNullOrWhiteSpace(Read<string>(adapter, "Meaning")), "player-visible recognized meaning");
        CommandEnvelope expected = Expected(manual, phrase);
        CommandResult normal = manual.Commands.Submit(expected);
        Check(normal.Accepted, "real normal-control oracle accepted");
        CommandEnvelope actual = Read<CommandEnvelope>(adapter, "LastCommand");
        SameCommand(actual, expected);
        CommandResult? result = Read<CommandResult?>(adapter, "LastResult");
        Check(result.HasValue && result.Value.Accepted == normal.Accepted, "same command result");
        Check(voice.Commands.CommandLog.Count == before + 1, "exactly one original arbitrator submission");
        CommandLogEntry logged = voice.Commands.CommandLog[before];
        Check(logged.Command == actual.Id && logged.Kind == expected.Kind && logged.Accepted,
            "LastCommand is the command actually arbitrated");
        Check(State(voice) == State(manual), "same authoritative result as manual command");
        ActionEntry current = voice.Commands.GetCurrent(voice.Alex.Id);
        if (phrase.Kind == CommandKind.DirectActivity || phrase.Kind == CommandKind.TravelTo)
            Check(current != null && current.Source == CommandSource.VoiceMapped
                && current.Owner == ActionOwner.Player, "voice uses normal Player action queue");
        Call(adapter, "Complete", request.Id, Frame(phrase.Text));
        Check(voice.Commands.CommandLog.Count == before + 1, "duplicate frame executes nothing twice");
    }
    private static void InertText(string text)
    {
        var f = new Fixture(); var control = new Fixture(); object adapter = f.Adapter();
        GatewayRequest request = f.Request(adapter);
        Call(adapter, "Complete", request.Id, Frame(text));
        Check(f.Commands.CommandLog.Count == 0 && Read<CommandEnvelope>(adapter, "LastCommand") == null,
            "unsupported text submits zero commands");
        Check(!Read<VoiceTranscript>(adapter, "Transcript").Meaning.HasValue
            && !string.IsNullOrWhiteSpace(Read<string>(adapter, "Prompt")), "repeat prompt without guessed meaning");
        f.Clock.AdvanceRealSeconds(Core, 1.0); control.Clock.AdvanceRealSeconds(Core, 1.0);
        Check(f.Clock.TotalTicks == control.Clock.TotalTicks && f.Clock.TotalTicks == 10,
            "identical ten-tick evolution");
        Check(State(f) == State(control), "no voice-caused mutation versus same-seed evolving control");
    }
    private static void Ambiguity(bool choose)
    {
        var f = new Fixture(); var control = new Fixture(); object adapter = f.Adapter();
        GatewayRequest request = f.Request(adapter);
        Call(adapter, "Complete", request.Id, Frame("follow Sam"));
        var candidates = Read<IReadOnlyList<CommandEnvelope>>(adapter, "Candidates");
        Check(candidates.Count == 2 && f.Commands.CommandLog.Count == 0,
            "Sam/Samantha candidates displayed before any execution");
        var ids = new HashSet<ResidentId>();
        foreach (CommandEnvelope candidate in candidates)
        {
            Check(candidate.Kind == CommandKind.FollowResident && candidate.Resident.HasValue,
                "candidate is a supported follow meaning");
            ids.Add(candidate.Resident.Value);
        }
        Check(ids.Contains(f.Sam.Id) && ids.Contains(f.Samantha.Id)
            && !string.IsNullOrWhiteSpace(Read<string>(adapter, "Prompt")), "both named residents and choice prompt");
        if (choose)
        {
            CommandResult result = (CommandResult)Call(adapter, "Choose", 1);
            Check(result.Accepted && f.Commands.CommandLog.Count == 1, "exactly one chosen ordinary command");
            Check(f.Commands.CommandLog[0].Resident == candidates[1].Resident
                && Read<CommandEnvelope>(adapter, "LastCommand").Source == CommandSource.VoiceMapped,
                "chosen candidate identity/source preserved");
            Call(adapter, "Choose", 1);
            Check(f.Commands.CommandLog.Count == 1, "consumed choice cannot submit twice");
        }
        else
        {
            Call(adapter, "Dismiss");
            f.Clock.AdvanceRealSeconds(Core, 1.0); control.Clock.AdvanceRealSeconds(Core, 1.0);
            Check(f.Commands.CommandLog.Count == 0 && State(f) == State(control), "dismiss executes nothing over ten ticks");
        }
    }
    private static void UnavailableActivity()
    {
        var f = new Fixture(); var manual = new Fixture();
        Check(f.Direct("ACT-EDU-02").Accepted && manual.Direct("ACT-EDU-02").Accepted, "existing queue in both worlds");
        ActionEntry previous = f.Commands.GetCurrent(f.Alex.Id);
        object adapter = f.Adapter(); GatewayRequest request = f.Request(adapter);
        CommandResult normal = manual.Commands.Submit(manual.Envelope(CommandKind.DirectActivity,
            CommandSource.PlayerHuman, manual.Alex.Id, "ACT-EDU-01"));
        Check(!normal.Accepted && normal.Reason == RejectReason.StageIneligible, "real adult school refusal");
        Call(adapter, "Complete", request.Id, Frame("direct Alex to attend school"));
        CommandResult? result = Read<CommandResult?>(adapter, "LastResult");
        Check(result.HasValue && !result.Value.Accepted && result.Value.Reason == normal.Reason,
            "voice gets ordinary StageIneligible reason");
        Check(f.Commands.CommandLog.Count == 2 && !f.Commands.CommandLog[1].Accepted,
            "unavailable mapping reaches original arbitrator");
        Check(object.ReferenceEquals(previous, f.Commands.GetCurrent(f.Alex.Id)) && State(f) == State(manual),
            "refusal preserves existing queue and state");
    }
    private static void NoConnectedSuccess(GatewayOutcome observed, int commands)
    {
        Check(observed != GatewayOutcome.Applied && commands == 0,
            "denied frame reported as connected success or executed a command");
    }
    private static void DeniedObservationOracle()
    {
        var f = new Fixture();
        GatewayRequest request = f.Boundary.RequestService(null, GatewayPurpose.VoiceTranscription, Endpoint(), true, true);
        Check(request != null, "real boundary request for denied observation");
        f.Boundary.CompleteFailure(request.Id, GatewayOutcome.Denied);
        GatewayOutcome observed = f.Boundary.DecisionLog[0].Outcome;
        Check(observed == GatewayOutcome.Denied, "real recorded-denial producer");
        if (negativeFixture) observed = GatewayOutcome.Applied;
        NoConnectedSuccess(observed, f.Commands.CommandLog.Count);
    }
    private static void DeniedCompletion()
    {
        var f = new Fixture(); object adapter = f.Adapter(); GatewayRequest request = f.Request(adapter);
        Call(adapter, "Complete", request.Id, DeniedFrame);
        Check(f.Boundary.DecisionLog.Count > 0, "denied response visibly logged");
        foreach (ProposalLogEntry entry in f.Boundary.DecisionLog)
            NoConnectedSuccess(entry.Outcome, f.Commands.CommandLog.Count);
        Check(Read<CommandEnvelope>(adapter, "LastCommand") == null && State(f) == State(new Fixture()),
            "denial has no command or authoritative mutation");
        Check(!string.IsNullOrWhiteSpace(Read<string>(adapter, "Prompt")), "denial explains manual alternative");
        Check(f.Direct("ACT-EDU-02").Accepted, "manual action after provider denial");
    }
    private static void RefusedRequestManual(bool mic, bool provisioned, bool credential)
    {
        var f = new Fixture(); object adapter = f.Adapter();
        GatewayRequest request = (GatewayRequest)Call(adapter, "Request", Endpoint(provisioned), credential, true, mic);
        Check(request == null && f.Boundary.PendingCount == 0 && f.Commands.CommandLog.Count == 0,
            "denial/unavailable/no-key issues no voice request or command");
        Check(!string.IsNullOrWhiteSpace(Read<string>(adapter, "Prompt")), "manual fallback explained");
        foreach (ProposalLogEntry entry in f.Boundary.DecisionLog) NoConnectedSuccess(entry.Outcome, 0);
        ManualFlows(f);
    }
    private static void ManualFlows(Fixture f)
    {
        foreach (Phrase phrase in Phrases)
        {
            Prepare(f, phrase);
            Check(f.Commands.Submit(Expected(f, phrase)).Accepted, "ordinary command remains available: " + phrase.Text);
        }
        var input = new InputMap(f.Access, f.Clock);
        bool paused = f.Clock.Paused;
        Check(input.TryActivate(f.Access.BindingFor("Pause")) && f.Clock.Paused != paused,
            "existing keyboard pause remains live");
        var selection = new Selection(f.World, f.Nav, f.Access);
        Check(selection.FocusResident(f.Alex.Id), "existing resident selection remains live");
        var menu = new ActionMenu(f.World, f.Commands, f.Access, input);
        BlockSiteSlot slot = f.Work.Slots[1];
        menu.Open(f.Alex.Id, slot.Object, slot.Id, new List<AvailableAction> {
            new AvailableAction(new ActivityId(slot.ActivityCode), (long)slot.Object) });
        Check(menu.TryClick("action:" + slot.ActivityCode + ":Immediate") && menu.Message.StartsWith("Accepted", StringComparison.Ordinal),
            "existing pointer action menu remains live");
    }
    // Destination construction uses the real catalog/NAV dependencies once
    // available; the unfixed adapter still runs the identical regressions.
    private static object DestinationAdapter(Fixture f)
    {
        Type type = typeof(ProposalBoundary).Assembly.GetType("Blockparty.AI.VoiceAdapter");
        ConstructorInfo ctor = type.GetConstructor(new[] { typeof(WorldState), typeof(ProposalBoundary),
            typeof(CommandArbitrator), typeof(BlockSites), typeof(ActivityExecutor) });
        return ctor == null ? f.Adapter() : ctor.Invoke(new object[] { f.World, f.Boundary, f.Commands, f.Sites, f.Executor });
    }
    private static void RejectedAmbiguity(string terminal)
    {
        var f = new Fixture(); object adapter = f.Adapter(); GatewayRequest request = f.Request(adapter);
        if (terminal == "stale")
            Check(f.Commands.Submit(f.Envelope(CommandKind.OpenMap, CommandSource.PlayerHuman)).Accepted, "newer manual map");
        else if (terminal == "cancel") Check(f.Boundary.Cancel(request.Id), "real cancellation");
        else { f.Boundary.AdvanceRealSeconds(10); f.Boundary.Tick(); }
        int before = f.Commands.CommandLog.Count;
        ProposalVerdict verdict = (ProposalVerdict)Call(adapter, "Complete", request.Id, Frame("follow Sam"));
        Check(verdict != ProposalVerdict.Applied && verdict != ProposalVerdict.HeldForPause, "boundary rejects terminal response");
        Check(Read<IReadOnlyList<CommandEnvelope>>(adapter, "Candidates").Count == 0, "F1 rejected ambiguity offers no choices");
        Check(!((CommandResult)Call(adapter, "Choose", 0)).Accepted && f.Commands.CommandLog.Count == before,
            "F1 rejected response cannot execute through Choose");
        Check(f.Direct("ACT-EDU-02").Accepted, "manual play after rejected ambiguity");
    }
    private static void ChoiceAfterManual(bool residentCommand)
    {
        var f = new Fixture(); object adapter = f.Adapter(); GatewayRequest request = f.Request(adapter);
        Call(adapter, "Complete", request.Id, Frame("follow Sam"));
        Check(Read<IReadOnlyList<CommandEnvelope>>(adapter, "Candidates").Count == 2, "fresh ambiguous choices");
        Check(f.Commands.Submit(f.Envelope(residentCommand ? CommandKind.FollowResident : CommandKind.OpenMap,
            CommandSource.PlayerHuman, residentCommand ? f.Sam.Id : (ResidentId?)null)).Accepted, "newer manual command");
        Check(!((CommandResult)Call(adapter, "Choose", 0)).Accepted && f.Commands.CommandLog.Count == 1,
            "F1 pending choice cannot override newer manual command");
    }
    private static void PausedResume(bool newerPause)
    {
        var f = new Fixture();
        if (!newerPause) f.Clock.Pause();
        object adapter = f.Adapter(); GatewayRequest request = f.Request(adapter);
        if (newerPause) Check(f.Commands.Submit(f.Envelope(CommandKind.Pause, CommandSource.PlayerHuman)).Accepted, "manual pause");
        Call(adapter, "Complete", request.Id, Frame("resume"));
        Check(f.Clock.Paused == newerPause, "F2 stale resume must preserve manual pause; fresh paused resume must work");
        Check(f.Boundary.PendingCount == 0 && f.Boundary.HeldCount == 0 && f.Boundary.DecisionLog.Count == 1,
            "F2 terminal boundary accounting before command mutation");
        Check(f.Boundary.DecisionLog[0].Outcome == (newerPause ? GatewayOutcome.Stale : GatewayOutcome.Applied),
            "F2 original freshness determines recorded outcome");
        Check(f.Commands.CommandLog.Count == 1, "F2 only fresh voice or newer manual command executes");
    }
    private static void DestinationAfterPurchase()
    {
        var f = new Fixture();
        var accounts = new Accounts(f.World); accounts.SetSharedBalance(Core, f.Alex.Household, 100);
        var housing = new Housing(f.World, accounts, new Ownership(f.World, accounts), f.Executor, new AppearanceStore());
        housing.RegisterFurniture(Core, "sofa", 75, 1);
        ObjectId sofa;
        Check(housing.BuyFurniture(Core, f.World.AllocateAction(Core), f.Alex.Household, "sofa", f.Alex.Home,
            new ApproachPose(0, 0, 0), AccountRef.Shared(f.Alex.Household), out sofa).Accepted, "real post-sites furniture purchase");
        object adapter = DestinationAdapter(f); GatewayRequest request = f.Request(adapter);
        Call(adapter, "Complete", request.Id, Frame("direct Alex to office"));
        Check(Read<CommandEnvelope>(adapter, "LastCommand")?.TargetId == (long)f.Work.Object,
            "F3 named office retains BlockSites identity after object allocation");
        Check(Read<CommandResult?>(adapter, "LastResult")?.Accepted == true, "named office remains available after purchase");
    }
    private static void DestinationAvailability(bool reachable)
    {
        var f = new Fixture(); var manual = new Fixture();
        Check(f.Direct("ACT-EDU-02").Accepted && manual.Direct("ACT-EDU-02").Accepted, "existing study queues");
        ActionEntry previous = f.Commands.GetCurrent(f.Alex.Id);
        f.Executor.SetReachable(f.Work.Object, reachable); manual.Executor.SetReachable(manual.Work.Object, reachable);
        object adapter = DestinationAdapter(f); GatewayRequest request = f.Request(adapter);
        CommandResult normal = manual.Nav.TravelTo(manual.Alex.Id, (long)manual.Work.Object, QueueMode.Immediate, TravelTargetKind.Object);
        Call(adapter, "Complete", request.Id, Frame("direct Alex to office"));
        CommandResult? voice = Read<CommandResult?>(adapter, "LastResult");
        Check(voice.HasValue && voice.Value.Accepted == normal.Accepted && voice.Value.Reason == normal.Reason,
            "F4 voice uses ordinary navigation availability and reason");
        Check(reachable ? f.Commands.GetCurrent(f.Alex.Id)?.TargetId == (long)f.Work.Object
            : object.ReferenceEquals(previous, f.Commands.GetCurrent(f.Alex.Id)), "F4 unavailable destination preserves queue");
        Check(f.Commands.CommandLog.Count == manual.Commands.CommandLog.Count, "F4 unavailable destination executes nothing");
        if (!reachable) Check(f.Direct("ACT-EDU-02").Accepted, "manual play after unavailable destination");
    }
    private static void DisplacedChoices(string replacement)
    {
        var f = new Fixture(); object adapter = f.Adapter();
        GatewayRequest first = f.Request(adapter), second = f.Request(adapter);
        Call(adapter, "Complete", first.Id, Frame("follow Sam"));
        Check(Read<IReadOnlyList<CommandEnvelope>>(adapter, "Candidates").Count == 2, "first response offers choices");
        Call(adapter, "Complete", second.Id, replacement);
        int before = f.Commands.CommandLog.Count;
        Check(Read<IReadOnlyList<CommandEnvelope>>(adapter, "Candidates").Count == 0,
            "F6 replacement response clears displaced choices");
        Check(!((CommandResult)Call(adapter, "Choose", 0)).Accepted && f.Commands.CommandLog.Count == before,
            "F6 displaced response cannot submit");
        Check(f.Direct("ACT-EDU-02").Accepted, "manual play after replacement response");
    }
    private static void SelectedMeaning()
    {
        var f = new Fixture(); object adapter = f.Adapter(); GatewayRequest request = f.Request(adapter);
        Call(adapter, "Complete", request.Id, Frame("follow Sam"));
        Check(((CommandResult)Call(adapter, "Choose", 1)).Accepted, "chosen Samantha command accepted");
        VoiceTranscript selected = Read<VoiceTranscript>(adapter, "Transcript");
        Check(selected.Text == "follow Sam" && selected.Meaning == VoiceMeaning.FollowResident && selected.ExecutesCommand,
            "F7 chosen typed meaning published with original transcript");
        Check(Read<string>(adapter, "Meaning") == "Follow resident Samantha", "F7 chosen display meaning published");
    }
    public static int Main(string[] args)
    {
        negativeFixture = Array.IndexOf(args, "negative-fixture") >= 0;
        // Establish real fixture/oracle validity even while VoiceAdapter is absent.
        Run("Fixture.NormalCommandProducers", () => {
            Check(Enum.GetValues(typeof(VoiceMeaning)).Length == 10, "recorded ten-meaning contract");
            foreach (Phrase phrase in Phrases) {
                var f = new Fixture(); Prepare(f, phrase);
                Check(f.Commands.Submit(Expected(f, phrase)).Accepted, "normal oracle " + phrase.Text);
                Check(State(f).Length > 0, "public world snapshot serializes");
            }
            ManualFlows(new Fixture());
        });
        Run("DeniedObservationOracle", DeniedObservationOracle);
        foreach (Phrase phrase in Phrases) Run("SupportedPairs." + phrase.Kind, () => SupportedPair(phrase));
        foreach (string text in new[] { "", "purple banana tuesday", "give her money", "make him happy", "delete the house" })
            Run("InertText." + (text.Length == 0 ? "Empty" : text), () => InertText(text));
        Run("AmbiguityChoice", () => Ambiguity(true));
        Run("AmbiguityDismiss", () => Ambiguity(false));
        Run("UnavailableActivity", UnavailableActivity);
        Run("DeniedFrame", DeniedCompletion);
        Run("RefusedRequestManual.MicrophoneDenied", () => RefusedRequestManual(false, true, true));
        Run("RefusedRequestManual.ServiceUnavailable", () => RefusedRequestManual(true, false, true));
        Run("RefusedRequestManual.NoCredential", () => RefusedRequestManual(true, true, false));
        foreach (string terminal in new[] { "stale", "cancel", "timeout" })
            Run("F1.RejectedAmbiguity." + terminal, () => RejectedAmbiguity(terminal));
        Run("F1.ChoiceAfterManual.World", () => ChoiceAfterManual(false));
        Run("F1.ChoiceAfterManual.Resident", () => ChoiceAfterManual(true));
        Run("F2.LateResume", () => PausedResume(true));
        Run("F2.FreshPausedResume", () => PausedResume(false));
        Run("F3.DestinationAfterPurchase", DestinationAfterPurchase);
        Run("F4.UnavailableDestination", () => DestinationAvailability(false));
        Run("F4.AvailableDestination", () => DestinationAvailability(true));
        Run("F6.DeniedDisplacesChoices", () => DisplacedChoices(DeniedFrame));
        Run("F6.MalformedDisplacesChoices", () => DisplacedChoices("{}"));
        Run("F6.UnknownDisplacesChoices", () => DisplacedChoices(Frame("purple banana")));
        Run("F6.SupportedDisplacesChoices", () => DisplacedChoices(Frame("open map")));
        Run("F7.SelectedMeaning", SelectedMeaning);
        if (failures == 0) Console.WriteLine("VOICE ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
