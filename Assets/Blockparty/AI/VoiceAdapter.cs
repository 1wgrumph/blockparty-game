// VOICE@1: Muse Voice transcription into the ordinary command path (BP-AI-08,
// BP-AI-09, BP-AI-10, CONTRACT-BP-06-VOICE). Consumes AI-BOUNDARY@1 (typed
// VoiceTranscription requests, terminal outcomes and the decision log),
// COMMAND@1 (CommandArbitrator and the one VoiceMapped set) and
// BLOCK-SITES@1 (office destination). Plain C# 9, no UnityEngine dependency.
// No network and no credential reaches this file: the boundary takes
// provisioning plus hasCredential presence only. The adapter parses final
// transcript frames locally and maps only the recorded supported meanings;
// empty, unrecognized or ambiguous text submits no command. Every supported
// meaning becomes a normal CommandEnvelope with Source VoiceMapped, the live
// resident/world sequence and Immediate mode, so selection, follow, queue
// ownership, refusal reasons and manual controls stay on the existing path.
// Denied, unavailable, missing-key, malformed and duplicate frames leave the
// world untouched and keep keyboard/mouse/touch play available.
// JSON: Unity's Assembly-CSharp has no System.Text.Json, so frames parse
// through SaveV1's strict reader (the SAM/IMAGE pattern).
// Fast Enter Play Mode: no static mutable state and no wall-clock reads.
using System;
using System.Collections.Generic;
using System.Text;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;

namespace Blockparty.AI
{
    public sealed class VoiceAdapter
    {
        private const string RepeatPrompt =
            "I did not catch a supported command. Repeat it or use the keyboard, mouse or touch controls.";
        private const string ChoicePrompt =
            "That command matches more than one resident. Choose one of the shown meanings or use the controls.";
        private const string DeniedPrompt =
            "Voice transcription was denied. Keyboard, mouse and touch controls remain available.";
        private const string UnavailablePrompt =
            "Voice transcription is unavailable. Keyboard, mouse and touch controls remain available.";
        private const string BadFramePrompt =
            "The voice response was not usable. Repeat the command or use the controls.";
        private const string ActivatorPrompt =
            "Start with \"Hey Meta\" to give a command without holding the talk key.";
        public const string ListeningPrompt = "Listening… say the command.";

        // Wake phrases as Muse Voice writes them ("Hey Meta,", "Hey, Meta.", "OK Meta", "Meta, ...").
        private static readonly string[] WakePhrases = { "hey meta", "hi meta", "ok meta", "okay meta", "meta" };

        // True when the text starts with a wake phrase; rest is the normalised command after it.
        public static bool StripWake(string heard, out string rest)
        {
            string p = Normalize(heard);
            rest = string.Empty;
            foreach (string wake in WakePhrases)
            {
                if (p == wake) return true;
                if (!p.StartsWith(wake + " ", StringComparison.Ordinal)) continue;
                rest = p.Substring(wake.Length + 1);
                return true;
            }
            return false;
        }

        // The last final transcript was activator speech without a wake phrase (background talk).
        public bool Ambient { get; private set; }

        // The last final transcript was only the wake phrase; the caller listens for the command.
        public bool WakeOnly { get; private set; }

        private sealed class CommandPlan
        {
            public readonly CommandKind Kind;
            public readonly ResidentId? Resident;
            public readonly ActivityId? Activity;
            public readonly long? TargetId;
            public readonly CommandParameters Parameters;
            public readonly string Meaning;
            // VOICE-SOCIAL: set only for a party plan, which runs LiveSimulation.PlanParty like the plan dialog.
            public ResidentId[] Guests;
            public long Start;

            public CommandPlan(CommandKind kind, ResidentId? resident, ActivityId? activity,
                long? targetId, CommandParameters parameters, string meaning)
            {
                Kind = kind;
                Resident = resident;
                Activity = activity;
                TargetId = targetId;
                Parameters = parameters;
                Meaning = meaning ?? string.Empty;
            }
        }

        private readonly WorldState world;
        private readonly ProposalBoundary boundary;
        private readonly CommandArbitrator commands;
        private readonly BlockSites sites;
        private readonly ActivityExecutor executor;
        private readonly Dictionary<GatewayRequestId, GatewayRequest> pending =
            new Dictionary<GatewayRequestId, GatewayRequest>();
        private GatewayRequest choiceRequest;
        private List<CommandPlan> plans = new List<CommandPlan>();
        private List<CommandEnvelope> candidates = new List<CommandEnvelope>();
        private VoiceTranscript transcript = new VoiceTranscript(string.Empty, null);
        private string meaning = string.Empty;
        private string prompt = string.Empty;
        private CommandEnvelope lastCommand;
        private CommandResult? lastResult;
        private string refusal;
        private ResidentRecord actorOverride;   // natural phrasing names the actor instead of the selection   // VOICE-SOCIAL: why a recognised social phrase resolved to nothing

        public VoiceAdapter(WorldState world, ProposalBoundary boundary,
            CommandArbitrator commands)
        {
            if (world == null) throw new ArgumentNullException("world");
            if (boundary == null) throw new ArgumentNullException("boundary");
            if (commands == null) throw new ArgumentNullException("commands");
            this.world = world;
            this.boundary = boundary;
            this.commands = commands;
        }

        public VoiceAdapter(WorldState world, ProposalBoundary boundary,
            CommandArbitrator commands, BlockSites sites, ActivityExecutor executor)
            : this(world, boundary, commands)
        {
            if (sites == null) throw new ArgumentNullException("sites");
            if (executor == null) throw new ArgumentNullException("executor");
            this.sites = sites;
            this.executor = executor;
        }

        public VoiceTranscript Transcript { get { return transcript; } }

        // Player-visible recognized meaning; empty when nothing was supported.
        public string Meaning { get { return meaning; } }

        // Player-visible repeat/choice/manual-fallback message.
        public string Prompt { get { return prompt; } }

        public CommandEnvelope LastCommand { get { return lastCommand; } }

        public CommandResult? LastResult { get { return lastResult; } }

        public IReadOnlyList<CommandEnvelope> Candidates { get { return candidates; } }

        // VOICE-LIVE: the player-visible meaning of each candidate, in Choose order.
        public IReadOnlyList<string> CandidateMeanings
        {
            get { return plans.ConvertAll(p => p.Meaning); }
        }

        // VOICE-LIVE (M5-OD-38): player phrases applied before matching, and
        // the activator request kind whose transcript must start with "hey meta".
        public VoiceAliases Aliases { get; set; }

        public bool RequireActivator { get; set; }

        // VOICE-SOCIAL (M5-OD-53): the player's selected resident (the actor of
        // chat, friendly and party phrases), the live simulation that plans
        // parties, and the party venue the plan dialog uses.
        public Func<ResidentId?> SelectedResident { get; set; }

        public LiveSimulation Live { get; set; }

        public Func<long> PartyVenue { get; set; }

        // Issues one residentless VoiceTranscription request. Refusal happens
        // before the boundary is touched, so a denied microphone, an
        // unprovisioned adapter or a missing credential creates no pending
        // request and executes no command.
        public GatewayRequest Request(ServiceEndpoint endpoint, bool hasCredential,
            bool disclosureGiven, bool microphoneGranted)
        {
            Reset();
            if (!microphoneGranted)
            {
                prompt = DeniedPrompt;
                return null;
            }
            if (endpoint == null || !endpoint.Provisioned)
            {
                prompt = UnavailablePrompt;
                return null;
            }
            if (!hasCredential)
            {
                prompt = UnavailablePrompt;
                return null;
            }
            GatewayRequest request = boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, endpoint, hasCredential, disclosureGiven);
            if (request == null)
            {
                prompt = UnavailablePrompt;
                return null;
            }
            pending[request.Id] = request;
            prompt = "Listening for a supported command.";
            return request;
        }

        // Delivers one final transcript or terminal error frame. A duplicate or
        // unknown request id is an idempotent no-op, so one frame executes at
        // most one command. Partial transcript frames execute nothing and keep
        // the request open for its final frame.
        public ProposalVerdict Complete(GatewayRequestId request, string frame)
        {
            GatewayRequest original;
            if (!pending.TryGetValue(request, out original))
            {
                return ProposalVerdict.DuplicateIgnored;
            }
            ParsedFrame parsed = Parse(frame);
            if (parsed.Kind == FrameKind.Partial)
            {
                prompt = "Listening for the final transcript.";
                return ProposalVerdict.DuplicateIgnored;
            }
            pending.Remove(request);
            Reset();
            if (parsed.Kind == FrameKind.Error)
            {
                ProposalVerdict failed = boundary.CompleteFailure(request, parsed.Outcome);
                transcript = new VoiceTranscript(string.Empty, null);
                meaning = string.Empty;
                prompt = parsed.Outcome == GatewayOutcome.Denied
                    ? DeniedPrompt
                    : "Voice transcription failed. Repeat it or use the controls.";
                return failed;
            }
            if (parsed.Kind != FrameKind.Final)
            {
                ProposalVerdict malformed = boundary.CompleteService(request, false, "BadFrame");
                transcript = new VoiceTranscript(string.Empty, null);
                meaning = string.Empty;
                prompt = BadFramePrompt;
                return malformed;
            }
            return CompleteTranscript(original, parsed.Text);
        }

        // Takes one displayed ambiguous candidate. Only the chosen candidate
        // enters CommandArbitrator, and a second call cannot submit twice.
        public CommandResult Choose(int candidateIndex)
        {
            if (candidateIndex < 0 || candidateIndex >= plans.Count)
            {
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            CommandPlan plan = plans[candidateIndex];
            CommandEnvelope candidate = candidates[candidateIndex];
            ResidentRecord resident;
            bool fresh = choiceRequest != null && choiceRequest.Epoch == world.Epoch
                && choiceRequest.ContextVersion.Sequence == commands.WorldSequence
                && (!candidate.Resident.HasValue
                    || (world.TryGetResident(candidate.Resident.Value, out resident)
                        && candidate.ResidentSequence == resident.ResidentSequence + 1u));
            plans = new List<CommandPlan>();
            candidates = new List<CommandEnvelope>();
            choiceRequest = null;
            if (!fresh)
            {
                prompt = "The voice response expired before it could execute. Repeat it or use the controls.";
                return CommandResult.Reject(RejectReason.StaleGeneration);
            }
            return Submit(plan);
        }

        // Dismissing the choice resolves nothing and leaves manual play intact.
        public void Dismiss()
        {
            plans = new List<CommandPlan>();
            candidates = new List<CommandEnvelope>();
            choiceRequest = null;
            prompt = "No command executed. Keyboard, mouse and touch controls remain available.";
        }

        private ProposalVerdict CompleteTranscript(GatewayRequest request, string text)
        {
            // META-LIVE (owner live test 2026-11-04): the wake phrase is stripped on every path,
            // push-to-talk included; only the activator requires it.
            string heard;
            bool woke = StripWake(text, out heard);
            if (!woke) heard = text;
            bool prefixed = woke || !RequireActivator;
            Ambient = !prefixed;
            WakeOnly = woke && Normalize(heard).Length == 0;
            if (prefixed && Aliases != null) heard = Aliases.Apply(heard);
            List<CommandPlan> resolved = prefixed && !WakeOnly ? Resolve(heard) : new List<CommandPlan>();
            ProposalVerdict verdict = CompleteValidatedService(request.Id);
            transcript = new VoiceTranscript(text, null);
            if (verdict != ProposalVerdict.Applied)
            {
                prompt = "The voice response expired before it could execute. Repeat it or use the controls.";
                return verdict;
            }
            if (lookPhrase != null && LookRequested != null) LookRequested(lookPhrase);
            if (golfResident.HasValue && MinigolfRequested != null) MinigolfRequested(golfResident.Value);
            if (resolved.Count == 0)
            {
                prompt = WakeOnly ? ListeningPrompt : prefixed ? refusal ?? RepeatPrompt : ActivatorPrompt;
                // An unknown place is worth asking the model about; its refusal stays the fallback reply.
                NeedsInterpretation = prefixed && !WakeOnly && (refusal == null || placeRefusal) && Normalize(heard).Length > 0;
                interpretText = NeedsInterpretation ? heard : null;
                fallbackPrompt = placeRefusal ? refusal : null;
                return verdict;
            }
            if (resolved.Count > 1)
            {
                prompt = ChoicePrompt;
                choiceRequest = request;
                BuildCandidates(resolved);
                return verdict;
            }
            Submit(resolved[0]);
            return verdict;
        }

        private ProposalVerdict CompleteValidatedService(GatewayRequestId request)
        {
            bool paused = commands.Clock.Paused;
            // Synchronous validation must finish before a voice Resume changes its own stamp.
            if (paused) commands.Clock.Resume();
            try { return boundary.CompleteService(request, true, null); }
            finally { if (paused) commands.Clock.Pause(); }
        }

        private CommandResult Submit(CommandPlan plan)
        {
            transcript = new VoiceTranscript(transcript.Text, MeaningFor(plan.Kind));
            meaning = plan.Meaning;
            if (plan.Guests != null)
            {
                string summary;
                long venue = PartyVenue == null ? 0L : PartyVenue();
                PartyPlan made = Live.PlanParty(plan.Resident.Value, plan.Start, plan.Guests, venue, out summary);
                lastResult = made != null ? CommandResult.Accept() : CommandResult.Reject(RejectReason.InvalidParameters);
                prompt = summary;
                return lastResult.Value;
            }
            if (plan.Kind == CommandKind.TravelTo)
            {
                CommandResult available = CommandResult.Accept();
                try
                {
                    if (!executor.IsReachable((ObjectId)plan.TargetId.Value)) available = CommandResult.Reject(RejectReason.NoPath);
                }
                catch (ArgumentException) { available = CommandResult.Reject(RejectReason.TargetMissing); }
                if (!available.Accepted)
                {
                    lastResult = available;
                    prompt = plan.Meaning + " was refused: " + available.Reason + ".";
                    return lastResult.Value;
                }
            }
            CommandEnvelope envelope = BuildEnvelope(plan, true);
            CommandResult result = commands.Submit(envelope);
            lastCommand = envelope;
            lastResult = result;
            prompt = result.Accepted
                ? "Doing: " + plan.Meaning + "."
                : plan.Meaning + " was refused: " + result.Reason + ".";
            return result;
        }

        private void BuildCandidates(List<CommandPlan> resolved)
        {
            plans = new List<CommandPlan>(resolved);
            candidates = new List<CommandEnvelope>(resolved.Count);
            for (int i = 0; i < resolved.Count; i++)
            {
                candidates.Add(BuildEnvelope(resolved[i], false));
            }
        }

        private CommandEnvelope BuildEnvelope(CommandPlan plan, bool allocateId)
        {
            uint sequence = commands.WorldSequence + 1u;
            ResidentRecord record;
            if (plan.Resident.HasValue
                && world.TryGetResident(plan.Resident.Value, out record))
            {
                sequence = record.ResidentSequence + 1u;
            }
            CommandId id = allocateId
                ? world.AllocateCommand(StateWriter.SimCore)
                : default(CommandId);
            return new CommandEnvelope(id, world.Epoch, world.GameSeconds,
                CommandSource.VoiceMapped, sequence, plan.Kind, plan.Resident,
                plan.Activity, plan.TargetId, plan.Parameters);
        }

        // The fixed VOICE@1 forms first (instant), then natural phrasing of the same commands.
        private List<CommandPlan> Resolve(string text)
        {
            string phrase = Normalize(text);
            string fixedForm;
            if (Synonyms.TryGetValue(StripPolite(phrase), out fixedForm)) phrase = fixedForm;
            List<CommandPlan> resolved = ResolveFixed(phrase);
            return resolved.Count == 0 && refusal == null ? ResolveNatural(StripPolite(phrase)) : resolved;
        }

        private static readonly Dictionary<string, string> Synonyms = new Dictionary<string, string>
        {
            { "pause the game", "pause" }, { "pause game", "pause" }, { "unpause", "resume" },
            { "resume the game", "resume" }, { "resume game", "resume" }, { "continue", "resume" },
            { "open the map", "open map" }, { "show the map", "open map" }, { "show map", "open map" },
            { "save", "save game" }, { "save the game", "save game" }, { "stop follow", "stop following" },
            // META-LIVE: everyday speed phrasing (live test 2026-11-04).
            { "speed up", "set speed fast" }, { "speed things up", "set speed fast" }, { "go faster", "set speed fast" },
            { "faster", "set speed fast" }, { "fast forward", "set speed fast" }, { "slow down", "set speed normal" },
            { "normal speed", "set speed normal" }, { "slow it down", "set speed normal" },
        };

        private static string StripPolite(string phrase)
        {
            foreach (string lead in new[] { "please ", "can you ", "could you ", "okay ", "ok " })
                if (phrase.StartsWith(lead, StringComparison.Ordinal)) phrase = phrase.Substring(lead.Length);
            if (phrase.EndsWith(" please", StringComparison.Ordinal)) phrase = phrase.Substring(0, phrase.Length - 7);
            return phrase.Trim();
        }

        private List<CommandPlan> ResolveFixed(string phrase)
        {
            List<CommandPlan> resolved = new List<CommandPlan>();
            if (phrase.Length == 0) return resolved;
            if (phrase == "pause")
            {
                resolved.Add(SimplePlan(CommandKind.Pause, "Pause"));
                return resolved;
            }
            if (phrase == "resume")
            {
                resolved.Add(SimplePlan(CommandKind.Resume, "Resume"));
                return resolved;
            }
            if (phrase == "stop following")
            {
                resolved.Add(SimplePlan(CommandKind.StopFollowing, "Stop following"));
                return resolved;
            }
            if (phrase == "open map")
            {
                resolved.Add(SimplePlan(CommandKind.OpenMap, "Open map"));
                return resolved;
            }
            if (phrase == "save game")
            {
                resolved.Add(SimplePlan(CommandKind.SaveGame, "Save game"));
                return resolved;
            }
            if (phrase.StartsWith("set speed ", StringComparison.Ordinal))
            {
                SimulationSpeed? speed = ParseSpeed(phrase.Substring(10).Trim());
                if (speed.HasValue)
                {
                    resolved.Add(new CommandPlan(CommandKind.SetSpeed, null, null, null,
                        new CommandParameters.Speed(speed.Value),
                        "Set speed to " + speed.Value));
                }
                return resolved;
            }
            if (phrase.StartsWith("select ", StringComparison.Ordinal))
            {
                AddResidentPlans(resolved, phrase.Substring(7).Trim(),
                    CommandKind.SelectResident, null, null, "Select resident");
                return resolved;
            }
            // SAM look (owner 2026-11-04): a phrase that is not a resident is handed to LookRequested.
            // META-LIVE: finding a resident selects and follows them (FollowResident presents both).
            foreach (string lead in new[] { "find me ", "find ", "locate ", "show me ", "where is ", "where's ", "where are ",
                "look at ", "look for " })
            {
                if (!phrase.StartsWith(lead, StringComparison.Ordinal)) continue;
                string thing = phrase.Substring(lead.Length).Trim();
                foreach (string article in new[] { "the ", "a ", "an ", "my " })
                    if (thing.StartsWith(article, StringComparison.Ordinal)) { thing = thing.Substring(article.Length); break; }
                AddResidentPlans(resolved, thing, CommandKind.FollowResident, null, null, "Find and follow");
                if (resolved.Count == 0) Look(thing);
                return resolved;
            }
            if (phrase.StartsWith("follow ", StringComparison.Ordinal))
            {
                AddResidentPlans(resolved, phrase.Substring(7).Trim(),
                    CommandKind.FollowResident, null, null, "Follow resident");
                if (resolved.Count == 0) Look(phrase.Substring(7).Trim());
                return resolved;
            }
            if (phrase.StartsWith("cancel ", StringComparison.Ordinal))
            {
                string name = StripCancelSuffix(phrase.Substring(7).Trim());
                AddResidentPlans(resolved, name, CommandKind.CancelCurrent,
                    null, null, "Cancel current action for");
                return resolved;
            }
            if (phrase.StartsWith("direct ", StringComparison.Ordinal))
            {
                return ResolveDirect(phrase.Substring(7).Trim());
            }
            if (phrase.StartsWith("chat with ", StringComparison.Ordinal))
                return ResolveSocial(phrase.Substring(10).Trim(), "ACT-REL-01", "chat with");
            if (phrase.StartsWith("be friendly to ", StringComparison.Ordinal))
                return ResolveSocial(phrase.Substring(15).Trim(), "ACT-REL-02", "be friendly to");
            if (phrase.StartsWith("invite ", StringComparison.Ordinal))
                return ResolveParty(phrase.Substring(7).Trim(), true);
            // META-LIVE: "let's have a party tonight" and friends plan the selected resident's party.
            foreach (string party in new[] { "plan a party", "let's have a party", "lets have a party", "let's throw a party",
                "throw a party", "have a party", "host a party" })
                if (phrase == party || phrase.StartsWith(party + " ", StringComparison.Ordinal))
                    return ResolveParty(phrase.Substring(party.Length).Trim(), false);
            return resolved;
        }

        // Natural phrasing (owner playtest 2026-11-04): "[send|tell|have|ask|get|make|let] <name>[,] [to] <action>",
        // name first or after the verb, whole-word names only. Actions: work, study, school, a block site,
        // chat/talk with, be friendly/nice to, invite, plan/throw a party. Same plans as the fixed forms.
        private List<CommandPlan> ResolveNatural(string phrase)
        {
            List<CommandPlan> none = new List<CommandPlan>();
            foreach (string verb in new[] { "send ", "tell ", "have ", "ask ", "get ", "make ", "let " })
                if (phrase.StartsWith(verb, StringComparison.Ordinal)) { phrase = phrase.Substring(verb.Length); break; }
            List<ResidentRecord> actors;
            int length = LeadingName(phrase, out actors);
            if (length <= 0) return none;
            string name = phrase.Substring(0, length);
            string action = phrase.Substring(length).Trim();
            if (action.StartsWith("to ", StringComparison.Ordinal)) action = action.Substring(3);
            foreach (string[] social in new[] {
                new[] { "chat with ", "ACT-REL-01", "chat with" }, new[] { "talk to ", "ACT-REL-01", "chat with" },
                new[] { "talk with ", "ACT-REL-01", "chat with" }, new[] { "be friendly to ", "ACT-REL-02", "be friendly to" },
                new[] { "be nice to ", "ACT-REL-02", "be friendly to" } })
            {
                if (!action.StartsWith(social[0], StringComparison.Ordinal)) continue;
                return AsActor(name, actors, () => ResolveSocial(action.Substring(social[0].Length).Trim(), social[1], social[2]));
            }
            if (action.StartsWith("invite ", StringComparison.Ordinal))
                return AsActor(name, actors, () => ResolveParty(action.Substring(7).Trim(), true));
            foreach (string party in new[] { "plan a party", "throw a party", "host a party", "have a party" })
                if (action == party || action.StartsWith(party + " ", StringComparison.Ordinal))
                    return AsActor(name, actors, () => ResolveParty(action.Substring(party.Length).Trim(), false));
            string place = action;
            foreach (string lead in new[] { "go to the ", "go to ", "go ", "head to the ", "head to ", "walk to the ", "walk to ", "the " })
                if (place.StartsWith(lead, StringComparison.Ordinal)) { place = place.Substring(lead.Length); break; }
            if (place == "work" || place == "the cafe to work")
            {
                List<CommandPlan> work = new List<CommandPlan>();
                long? slot = ResolveSlotObject("cafe", "ACT-WORK-01");
                if (!slot.HasValue) return work;
                foreach (ResidentRecord actor in actors)
                    work.Add(new CommandPlan(CommandKind.DirectActivity, actor.Id, new ActivityId("ACT-WORK-01"), slot,
                        new CommandParameters.Direct(QueueMode.Immediate), "Direct " + actor.DisplayName + " to work"));
                return work;
            }
            if (place == "play minigolf" || place == "play mini golf" || place == "play golf" || place == "minigolf"
                || place == "mini golf" || place == "golf" || place == "play some minigolf" || place == "play some mini golf")
            {
                golfResident = actors[0].Id;
                refusal = "Starting minigolf with " + actors[0].DisplayName + ". P: putt.";
                return none;
            }
            if (place == "do homework" || place == "do their homework") place = "study";
            if (place == "school" || place == "to school") place = "attend school";
            if (place != "study" && place != "attend school") place = place.Replace(' ', '-');
            List<CommandPlan> direct = ResolveDirect(name + " to " + place);
            if (direct.Count == 0 && place.Length > 0)
            {
                refusal = "I can send " + actors[0].DisplayName + " to work, study, school or a place on the block, not \"" + place.Replace('-', ' ') + "\".";
                placeRefusal = true;
            }
            return direct;
        }

        // The longest resident display name, or first name, that the phrase starts with as whole words.
        private int LeadingName(string phrase, out List<ResidentRecord> actors)
        {
            actors = new List<ResidentRecord>();
            int best = 0;
            foreach (ResidentRecord r in world.ResidentsSnapshot())
            {
                string full = Normalize(r.DisplayName);
                int space = full.IndexOf(' ');
                int length = 0;
                foreach (string n in space > 0 ? new[] { full, full.Substring(0, space) } : new[] { full })
                    if (n.Length > length && (phrase == n || phrase.StartsWith(n + " ", StringComparison.Ordinal))) length = n.Length;
                if (length == 0 || length < best) continue;
                if (length > best) { best = length; actors.Clear(); }
                actors.Add(r);
            }
            actors.Sort((a, b) => ((long)a.Id).CompareTo((long)b.Id));
            return best;
        }

        // Runs a selection-based resolver with the named resident as the actor.
        private List<CommandPlan> AsActor(string name, List<ResidentRecord> actors, Func<List<CommandPlan>> resolve)
        {
            if (actors.Count > 1)
            {
                refusal = "\"" + name + "\" matches " + string.Join(" and ", actors.ConvertAll(r => r.DisplayName).ToArray())
                    + ". Say the full name.";
                return new List<CommandPlan>();
            }
            actorOverride = actors[0];
            try { return resolve(); }
            finally { actorOverride = null; }
        }

        private long? ResolveSlotObject(string siteId, string code)
        {
            if (sites == null) return null;
            foreach (BlockSite site in sites.Sites)
                if (site.Id == siteId)
                    foreach (BlockSiteSlot slot in site.Slots)
                        if (slot.ActivityCode == code) return (long)slot.Object;
            return null;
        }

        // ---------- VOICE-SOCIAL (M5-OD-53) ----------

        // "chat with <name>" / "be friendly to <name>": the SOCIAL card's command,
        // DirectActivity with the partner as target, for the selected resident.
        private List<CommandPlan> ResolveSocial(string name, string code, string verb)
        {
            List<CommandPlan> resolved = new List<CommandPlan>();
            ResidentRecord actor;
            if (!TrySelected(verb, out actor)) return resolved;
            foreach (ResidentRecord partner in ResolveOthers(name, actor))
            {
                resolved.Add(new CommandPlan(CommandKind.DirectActivity, actor.Id, new ActivityId(code),
                    (long)partner.Id, new CommandParameters.Direct(QueueMode.Immediate),
                    actor.DisplayName + ": " + verb + " " + partner.DisplayName));
            }
            return resolved;
        }

        // "invite <name> [and <name>] [to the party] [now|tonight|tomorrow]" and
        // "plan a party [now|tonight|tomorrow]": the plan dialog's confirm, hosted
        // by the selected resident. Invite adds to the host's open plan.
        private List<CommandPlan> ResolveParty(string rest, bool invite)
        {
            List<CommandPlan> resolved = new List<CommandPlan>();
            ResidentRecord host;
            if (!TrySelected(invite ? "invite" : "plan a party", out host)) return resolved;
            if (Live == null) { refusal = "Party planning is not available here."; return resolved; }
            string names = rest, when = invite ? string.Empty : rest;
            if (invite)
            {
                foreach (string tail in new[] { " to the party", " to a party", " to my party" })
                {
                    int at = rest.IndexOf(tail, StringComparison.Ordinal);
                    if (at < 0) continue;
                    names = rest.Substring(0, at).Trim();
                    when = rest.Substring(at + tail.Length).Trim();
                    break;
                }
                // The party phrase is optional: a recognised time may follow the names directly.
                if (when.Length == 0)
                    for (int sp = names.IndexOf(' '); sp > 0; sp = names.IndexOf(' ', sp + 1))
                        if (PartyTime(names.Substring(sp + 1)) >= 0)
                        {
                            when = names.Substring(sp + 1);
                            names = names.Substring(0, sp).Trim();
                            break;
                        }
            }
            PartyPlan open = Live.OpenPlanFor(host.Id);
            bool own = open != null && open.Host == (long)host.Id;
            int time = PartyTime(when);
            if (time == -2) { refusal = "Parties can be planned for now, tonight or tomorrow."; return resolved; }
            long start = time >= 0 ? Live.PartyStartFor(time)
                : own ? open.Start : Live.PartyStartFor(Live.PartyStartFor(1) >= 0L ? 1 : 2);
            if (start < 0L) { refusal = "It is too late to plan a party for tonight. Say tomorrow."; return resolved; }
            List<ResidentId> guests = new List<ResidentId>();
            if (own) foreach (PartyInvite asked in open.Invites) guests.Add((ResidentId)asked.Guest);
            else if (!invite) guests.AddRange(Live.TopBonds(host.Id, 2));
            List<string> invited = new List<string>();
            if (invite)
            {
                foreach (string one in names.Split(new[] { " and " }, StringSplitOptions.RemoveEmptyEntries))
                {
                    List<ResidentRecord> found = ResolveOthers(one.Trim(), host);
                    if (found.Count == 0) return resolved;
                    if (found.Count > 1)
                    {
                        refusal = "\"" + one.Trim() + "\" matches " + string.Join(" and ", found.ConvertAll(r => r.DisplayName).ToArray())
                            + ". Say the full name.";
                        return resolved;
                    }
                    if (!guests.Contains(found[0].Id)) guests.Add(found[0].Id);
                    invited.Add(found[0].DisplayName);
                }
                if (invited.Count == 0) { refusal = "Say who to invite: \"invite\" and a name."; return resolved; }
            }
            CommandPlan plan = new CommandPlan(CommandKind.DirectActivity, host.Id, new ActivityId("ACT-PARTY-01"), null,
                new CommandParameters.Direct(QueueMode.Immediate),
                host.DisplayName + (invite ? ": invite " + string.Join(" and ", invited.ToArray()) + " to the party" : ": plan a party"));
            plan.Guests = guests.ToArray();
            plan.Start = start;
            resolved.Add(plan);
            return resolved;
        }

        // -1 when no time was said, 0..2 the dialog's PartyTimes, -2 for anything else.
        private static int PartyTime(string when)
        {
            if (when.StartsWith("on ", StringComparison.Ordinal) || when.StartsWith("for ", StringComparison.Ordinal))
                when = when.Substring(when.IndexOf(' ') + 1);
            if (when.Length == 0) return -1;
            if (when == "now" || when == "right now") return 0;
            if (when == "tonight" || when == "this evening") return 1;
            if (when == "tomorrow" || when == "tomorrow night" || when == "tomorrow evening") return 2;
            return -2;
        }

        private bool TrySelected(string verb, out ResidentRecord actor)
        {
            actor = actorOverride;
            if (actor != null) return true;
            ResidentId? selected = SelectedResident == null ? null : SelectedResident();
            if (selected.HasValue && world.TryGetResident(selected.Value, out actor)) return true;
            refusal = "Select a resident first, then say \"" + verb + "\" and a name.";
            return false;
        }

        // Living residents other than the actor whose name starts with the spoken
        // name (the menus offer the same set); sets the refusal when there are none.
        private List<ResidentRecord> ResolveOthers(string name, ResidentRecord actor)
        {
            List<ResidentRecord> all = ResolveResidents(name);
            List<ResidentRecord> others = all.FindAll(r => r.Id != actor.Id && r.IsAlive);
            if (others.Count > 0) return others;
            if (Normalize(name).Length == 0) refusal = "Say a resident's name after the command.";
            else if (all.Count == 0) refusal = "There is no resident called \"" + name + "\".";
            else if (all.Exists(r => r.Id == actor.Id)) refusal = actor.DisplayName + " cannot do that with themselves.";
            else refusal = all[0].DisplayName + " is not around any more.";
            return others;
        }

        private List<CommandPlan> ResolveDirect(string rest)
        {
            List<CommandPlan> resolved = new List<CommandPlan>();
            int split = rest.IndexOf(" to ", StringComparison.Ordinal);
            if (split <= 0) return resolved;
            string name = rest.Substring(0, split).Trim();
            string destination = rest.Substring(split + 4).Trim();
            List<ResidentRecord> residents = ResolveResidents(name);
            if (residents.Count == 0) return resolved;
            if (destination == "study")
            {
                for (int i = 0; i < residents.Count; i++)
                {
                    resolved.Add(new CommandPlan(CommandKind.DirectActivity,
                        residents[i].Id, new ActivityId("ACT-EDU-02"), null,
                        new CommandParameters.Direct(QueueMode.Immediate),
                        "Direct " + residents[i].DisplayName + " to study"));
                }
                return resolved;
            }
            if (destination == "attend school")
            {
                for (int i = 0; i < residents.Count; i++)
                {
                    resolved.Add(new CommandPlan(CommandKind.DirectActivity,
                        residents[i].Id, new ActivityId("ACT-EDU-01"), null,
                        new CommandParameters.Direct(QueueMode.Immediate),
                        "Direct " + residents[i].DisplayName + " to attend school"));
                }
                return resolved;
            }
            long? target = ResolveSiteObject(destination);
            if (!target.HasValue) return resolved;
            for (int i = 0; i < residents.Count; i++)
            {
                resolved.Add(new CommandPlan(CommandKind.TravelTo, residents[i].Id, null,
                    target, new CommandParameters.Travel(QueueMode.Immediate,
                        TravelTargetKind.Object),
                    "Travel " + residents[i].DisplayName + " to " + destination));
            }
            return resolved;
        }

        private void AddResidentPlans(List<CommandPlan> resolved, string name,
            CommandKind kind, ActivityId? activity, long? target, string label)
        {
            List<ResidentRecord> residents = ResolveResidents(name);
            for (int i = 0; i < residents.Count; i++)
            {
                resolved.Add(new CommandPlan(kind, residents[i].Id, activity, target,
                    CommandParameters.None.Instance,
                    label + " " + residents[i].DisplayName));
            }
        }

        private CommandPlan SimplePlan(CommandKind kind, string label)
        {
            return new CommandPlan(kind, null, null, null,
                CommandParameters.None.Instance, label);
        }

        private List<ResidentRecord> ResolveResidents(string spoken)
        {
            List<ResidentRecord> matches = new List<ResidentRecord>();
            string wanted = Normalize(spoken);
            if (wanted.Length == 0) return matches;
            ResidentRecord[] residents = world.ResidentsSnapshot();
            for (int i = 0; i < residents.Length; i++)
            {
                string name = Normalize(residents[i].DisplayName);
                if (name.Length > 0 && name.StartsWith(wanted, StringComparison.Ordinal))
                {
                    matches.Add(residents[i]);
                }
            }
            // META-LIVE: Muse Voice spells names by sound ("Ellie" for Eli, "Kara" for Cara); only when
            // nothing matched as spoken, compare spellings with k=c, doubled letters single, final y/ie/ey = i.
            if (matches.Count == 0 && wanted.IndexOf(' ') < 0)
                foreach (ResidentRecord r in residents)
                    if (Spelling(Normalize(r.DisplayName).Split(' ')[0]) == Spelling(wanted)) matches.Add(r);
            matches.Sort((a, b) => ((long)a.Id).CompareTo((long)b.Id));
            return matches;
        }

        private static string Spelling(string word)
        {
            StringBuilder b = new StringBuilder(word.Length);
            foreach (char raw in word)
            {
                char c = raw == 'k' ? 'c' : raw;
                if (b.Length == 0 || b[b.Length - 1] != c) b.Append(c);
            }
            string s = b.ToString();
            foreach (string end in new[] { "ie", "ey", "y" })
                if (s.Length > end.Length + 1 && s.EndsWith(end, StringComparison.Ordinal)) return s.Substring(0, s.Length - end.Length) + "i";
            return s;
        }

        private long? ResolveSiteObject(string name)
        {
            if (sites == null) return null;
            foreach (BlockSite site in sites.Sites)
                if (site.Id == name) return (long)site.Object;
            return null;
        }

        private static SimulationSpeed? ParseSpeed(string word)
        {
            if (word == "paused") return SimulationSpeed.Paused;
            if (word == "normal") return SimulationSpeed.Normal;
            if (word == "fast") return SimulationSpeed.Fast;
            if (word == "ultra") return SimulationSpeed.Ultra;
            return null;
        }

        private static VoiceMeaning MeaningFor(CommandKind kind)
        {
            switch (kind)
            {
                case CommandKind.SelectResident: return VoiceMeaning.SelectResident;
                case CommandKind.FollowResident: return VoiceMeaning.FollowResident;
                case CommandKind.StopFollowing: return VoiceMeaning.StopFollowing;
                case CommandKind.Pause: return VoiceMeaning.Pause;
                case CommandKind.Resume: return VoiceMeaning.Resume;
                case CommandKind.SetSpeed: return VoiceMeaning.SetSpeed;
                case CommandKind.CancelCurrent: return VoiceMeaning.CancelCurrent;
                case CommandKind.OpenMap: return VoiceMeaning.OpenMap;
                case CommandKind.SaveGame: return VoiceMeaning.SaveGame;
                default: return VoiceMeaning.DirectResident;
            }
        }

        private static string StripCancelSuffix(string name)
        {
            string suffix = " current action";
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name.Substring(0, name.Length - suffix.Length).Trim();
            }
            if (name.EndsWith("'s", StringComparison.Ordinal))
            {
                name = name.Substring(0, name.Length - 2).Trim();
            }
            return name;
        }

        private void Reset()
        {
            plans = new List<CommandPlan>();
            candidates = new List<CommandEnvelope>();
            choiceRequest = null;
            transcript = new VoiceTranscript(string.Empty, null);
            meaning = string.Empty;
            prompt = string.Empty;
            lastCommand = null;
            lastResult = null;
            refusal = null;
            placeRefusal = false;
            fallbackPrompt = null;
            Ambient = false;
            WakeOnly = false;
            NeedsInterpretation = false;
            interpretText = null;
            lookPhrase = null;
            golfResident = null;
        }

        // ---------- SAM look ----------
        // Raised once per final transcript asking to find something that is not a resident;
        // the SAM lane subscribes. The adapter runs no command for it.
        public event Action<string> LookRequested;

        private string lookPhrase;

        // Minigolf (owner 2026-11-04): "<name> play minigolf" starts a match for that resident;
        // the director runs it like the activity menu does. The adapter runs no command for it.
        public event Action<ResidentId> MinigolfRequested;

        private ResidentId? golfResident;

        private void Look(string thing)
        {
            if (thing.Length == 0) return;
            lookPhrase = thing;
            refusal = "Looking for " + thing + "…";   // the HUD reply; no command and no model fallback
        }

        // ---------- VOICE-AI (owner 2026-11-04): the Meta decision model as a fallback ----------
        // When the local grammar finds nothing, the transcript, the resident names and the
        // concrete commands the grammar accepts right now go to the decision model as one
        // "choice" question (the D1U request shape). Its pick is only ever one offered
        // command or "none"; the pick is resolved by the local grammar and submitted like
        // any voice command, so CommandArbitrator (or PlanParty) still decides. A split
        // pick asks the player which one; "none", refusals and failures get a HUD reply.

        public const string NoneOption = "none";
        public const string UnclearPrompt = "I could not work out a game command from that. Try \"tell <name> to go to work\" or use the controls.";
        private string interpretText;
        private bool placeRefusal;       // the refusal is "not a place I know", so the model may still map it
        private string fallbackPrompt;   // that refusal, shown when the model finds nothing either
        private GatewayRequest interpreting;
        private List<string> interpretOptions;

        // True when the last final transcript found no local command and may be interpreted.
        public bool NeedsInterpretation { get; private set; }

        // Every concrete fixed-form command the local grammar accepts for this world now.
        public List<string> InterpretOptions()
        {
            List<string> options = new List<string> { NoneOption, "pause", "resume", "open map", "save game", "stop following",
                "set speed normal", "set speed fast", "set speed ultra" };
            List<string> places = new List<string> { "work", "study", "attend school" };
            if (sites != null) foreach (BlockSite site in sites.Sites) places.Add(site.Id);
            ResidentRecord[] residents = world.ResidentsSnapshot();
            Array.Sort(residents, (a, b) => ((long)a.Id).CompareTo((long)b.Id));
            foreach (ResidentRecord r in residents)
            {
                if (!r.IsAlive) continue;
                string n = Normalize(r.DisplayName);
                options.Add("select " + n);
                options.Add("follow " + n);
                foreach (string place in places) options.Add(place == "work" ? "tell " + n + " to go to work" : "direct " + n + " to " + place);
                foreach (ResidentRecord o in residents)
                {
                    if (o.Id == r.Id || !o.IsAlive) continue;
                    string m = Normalize(o.DisplayName);
                    options.Add("have " + n + " chat with " + m);
                    options.Add("have " + n + " be friendly to " + m);
                    if (Live != null) options.Add("have " + n + " invite " + m + " to the party");
                }
                if (Live != null) options.Add("have " + n + " plan a party");
            }
            return options;
        }

        // Issues the interpretation request through the boundary (residentless, the voice purpose,
        // the decision model's endpoint). Null when nothing needs interpreting or the boundary refuses.
        public GatewayRequest RequestInterpretation(ServiceEndpoint endpoint, bool hasCredential)
        {
            if (!NeedsInterpretation || interpreting != null) return null;
            NeedsInterpretation = false;
            GatewayRequest request = boundary.RequestService(null, GatewayPurpose.VoiceTranscription, endpoint, hasCredential, true);
            if (request == null) return null;
            interpreting = request;
            interpretOptions = InterpretOptions();
            prompt = "Working out what you meant…";
            return request;
        }

        // META-LIVE (live 2026-11-04): api.meta.ai/v1/responses rejects the D1U "questions" body
        // (400 unknown parameter) and has no d1-ultra (404 model_not_found), so the question goes to
        // muse-spark-1.3 (owner decision 2026-11-04) in the endpoint's Responses shape: instructions plus the state as JSON text; the reply is the
        // one offered command, verbatim.
        public string InterpretationBody(string modelId)
        {
            JsonObject state = new JsonObject();
            state.Add("transcript", interpretText ?? string.Empty);
            JsonArray names = new JsonArray();
            foreach (ResidentRecord r in world.ResidentsSnapshot()) if (r.IsAlive) names.Add(new JsonString(r.DisplayName));
            state.Add("residents", names);
            ResidentId? selected = SelectedResident == null ? null : SelectedResident();
            ResidentRecord chosen;
            state.Add("selected_resident", selected.HasValue && world.TryGetResident(selected.Value, out chosen) ? chosen.DisplayName : string.Empty);
            JsonArray offered = new JsonArray();
            foreach (string option in interpretOptions ?? InterpretOptions()) offered.Add(new JsonString(option));
            state.Add("offered_commands", offered);
            StringBuilder input = new StringBuilder();
            state.Render(input);
            JsonObject reasoning = new JsonObject();
            reasoning.Add("effort", "medium");   // owner decision 2026-11-04: muse-spark-1.3 at medium
            JsonObject root = new JsonObject();
            root.Add("model", modelId);
            root.Add("reasoning", reasoning);
            root.Add("instructions", "The player of a life-simulation game spoke the transcript (speech-to-text, so names may be "
                + "misspelled: match them to the residents by sound). Reply with exactly one of offered_commands, copied verbatim, "
                + "that does what they asked, or none when nothing offered fits. Prefer the selected resident when no one is named. "
                + "No other text.");
            root.Add("input", input.ToString());
            StringBuilder text = new StringBuilder();
            root.Render(text);
            return text.ToString();
        }

        // Settles the interpretation from the HTTP status and body (status 0 = no reply in time,
        // -1 = no key). Returns the submitted command's result, or null when nothing ran.
        public CommandResult? CompleteInterpretation(GatewayRequestId request, int status, string body)
        {
            if (interpreting == null || interpreting.Id != request) return null;
            List<string> options = interpretOptions;
            interpreting = null;
            interpretOptions = null;
            string choice, second;
            double top, next;
            GatewayOutcome outcome = ParseChoice(status, body, out choice, out top, out second, out next);
            if (outcome != GatewayOutcome.Applied)
            {
                boundary.CompleteFailure(request, outcome);
                prompt = status == -1 ? "Voice understanding needs the Meta key. Use the fixed phrases or the controls." : fallbackPrompt ?? UnclearPrompt;
                return null;
            }
            bool offered = options.Contains(choice);
            ProposalVerdict verdict = offered ? CompleteValidatedService(request) : boundary.CompleteService(request, false, "NotOffered");
            if (!offered || verdict != ProposalVerdict.Applied || choice == NoneOption)
            {
                prompt = fallbackPrompt ?? UnclearPrompt;
                return null;
            }
            // A split pick (the runner-up holds at least half the winner's weight) asks instead of guessing.
            if (second != null && second != NoneOption && options.Contains(second) && next >= top / 2.0)
            {
                prompt = "Did you mean \"" + Describe(choice) + "\" or \"" + Describe(second) + "\"? Say it again.";
                return null;
            }
            refusal = null;
            List<CommandPlan> plans = ResolveFixed(Normalize(choice));
            if (plans.Count == 0 && refusal == null) plans = ResolveNatural(Normalize(choice));
            if (plans.Count != 1)
            {
                prompt = refusal ?? UnclearPrompt;
                return null;
            }
            return Submit(plans[0]);
        }

        private string Describe(string option)
        {
            List<CommandPlan> plans = ResolveFixed(Normalize(option));
            if (plans.Count == 0) plans = ResolveNatural(Normalize(option));
            return plans.Count == 1 ? plans[0].Meaning : option;
        }

        // The Responses reply's output_text (the live shape), or the D1U answers.command.choice
        // plus its optional distribution (option -> probability).
        private static GatewayOutcome ParseChoice(int status, string body, out string choice, out double top,
            out string second, out double next)
        {
            choice = second = null;
            top = next = 0.0;
            if (status == -1) return GatewayOutcome.Unprovisioned;
            if (status == 0) return GatewayOutcome.Timeout;
            if (status == 401 || status == 403 || status == 429) return GatewayOutcome.Denied;
            if (status < 200 || status >= 300) return GatewayOutcome.Malformed;
            try
            {
                JsonObject root = JsonParser.ParseSamFrame(body) as JsonObject;
                if (root == null || (root.Has("error") && !root.IsNull("error", "error"))) return GatewayOutcome.Malformed;
                if (root.Has("output"))
                {
                    JsonArray output = root.GetArray("output", "voice");
                    for (int i = 0; i < output.Count && choice == null; i++)
                    {
                        JsonObject item = output.GetObject(i, "voice.output");
                        if (item.GetString("type", "voice.output") != "message") continue;
                        JsonArray parts = item.GetArray("content", "voice.output.message");
                        for (int c = 0; c < parts.Count && choice == null; c++)
                        {
                            JsonObject part = parts.GetObject(c, "voice.output.message.content");
                            if (part.GetString("type", "voice.output.message.content") == "output_text")
                                choice = Normalize(part.GetString("text", "voice.output.message.content").Trim('\'', '`', ' ', '\n'));
                        }
                    }
                    top = 1.0;
                    return choice == null ? GatewayOutcome.Malformed : GatewayOutcome.Applied;
                }
                JsonObject answer = root.GetObject("answers", "voice").GetObject("command", "voice.answers");
                choice = answer.GetString("choice", "voice.answers.command");
                top = 1.0;
                if (answer.Has("distribution"))
                {
                    JsonObject distribution = answer.GetObject("distribution", "voice.answers.command");
                    top = 0.0;
                    foreach (string key in distribution.Keys())
                    {
                        double p = distribution.GetDouble(key, "voice.answers.command.distribution");
                        if (key == choice) top = p;
                        else if (p > next) { next = p; second = key; }
                    }
                }
                return GatewayOutcome.Applied;
            }
            catch (JsonSaveException) { return GatewayOutcome.Malformed; }
            catch (InvalidCastException) { return GatewayOutcome.Malformed; }
        }

        private enum FrameKind
        {
            Final,
            Partial,
            Error,
            Malformed,
        }

        private struct ParsedFrame
        {
            public FrameKind Kind;
            public string Text;
            public GatewayOutcome Outcome;
        }

        // Vendor frames are read through SaveV1's strict JSON reader. Fixed
        // reason codes only: raw error messages never reach the decision log.
        private static ParsedFrame Parse(string frame)
        {
            ParsedFrame parsed = new ParsedFrame();
            parsed.Kind = FrameKind.Malformed;
            if (string.IsNullOrEmpty(frame)) return parsed;
            try
            {
                JsonObject root = JsonParser.ParseSamFrame(frame) as JsonObject;
                if (root == null) return parsed;
                string type = root.GetString("type", "voice");
                if (type == "error")
                {
                    parsed.Kind = FrameKind.Error;
                    parsed.Outcome = ErrorOutcome(root);
                    return parsed;
                }
                if (type != "transcript" && type != "speechComplete") return parsed;
                string text = root.GetString("transcript", "voice");
                if (type == "transcript")
                {
                    if (!root.Has("final")) return parsed;
                    if (!root.GetBool("final", "voice"))
                    {
                        parsed.Kind = FrameKind.Partial;
                        parsed.Text = text;
                        return parsed;
                    }
                }
                parsed.Kind = FrameKind.Final;
                parsed.Text = text;
                return parsed;
            }
            catch (JsonSaveException) { return parsed; }
        }

        private static GatewayOutcome ErrorOutcome(JsonObject root)
        {
            if (!root.Has("message")) return GatewayOutcome.Malformed;
            try
            {
                string message = root.GetString("message", "voice");
                if (message.IndexOf("permission", StringComparison.OrdinalIgnoreCase) >= 0)
                    return GatewayOutcome.Denied;
                if (message.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)
                    return GatewayOutcome.Timeout;
                return GatewayOutcome.Malformed;
            }
            catch (JsonSaveException) { return GatewayOutcome.Malformed; }
        }

        private static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            text = text.Normalize(NormalizationForm.FormD);   // "café" -> "cafe" + combining mark, dropped below
            StringBuilder builder = new StringBuilder(text.Length);
            bool space = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                // Sentence punctuation is a space ("Eli, go to work."); apostrophes stay.
                if (char.IsWhiteSpace(c) || c == ',' || c == '.' || c == '!' || c == '?' || c == ';' || c == ':' || c == '"')
                {
                    if (builder.Length > 0) space = true;
                    continue;
                }
                if (space)
                {
                    builder.Append(' ');
                    space = false;
                }
                builder.Append(char.ToLowerInvariant(c));
            }
            while (builder.Length > 0
                && (builder[builder.Length - 1] == '.' || builder[builder.Length - 1] == '!'
                    || builder[builder.Length - 1] == ',' || builder[builder.Length - 1] == '?'))
            {
                builder.Length--;
            }
            return builder.ToString();
        }
    }
}
