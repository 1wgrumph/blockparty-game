// RELATION@1: post-activity completion boundary for relationships (BP-REL-01..05).
// Consumes STATE@1 (WorldState, bonds/feelings/memories, WorldRng) and the
// PERSONALITY@1 draw/probability rules (Autonomy statics, SocialDraw records).
// Plain C# 9 with no engine dependency. All world writes flow through audited
// WorldState methods with the caller StateWriter; Tick validates the writer
// the same way and drops feelings whose recorded game-time expiry passed.
// Feelings come only from completed events, one sentiment evaluation per
// participant, and memories only from participation or a completed Chat edge.
// Determinism: refused input consumes no draws and mutates nothing; accepted
// null draws come from this instance stream seeded from the world seed, so
// paired same-seed runs record identical draws and state. No static mutable
// state; durations read the shared game clock, never wall time.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;

namespace Blockparty.Simulation
{
    public sealed class RelationshipResult
    {
        public bool Applied { get; private set; }
        public string Reason { get; private set; }
        public EventId Event { get; private set; }
        public IReadOnlyList<SocialDraw> Draws { get; private set; }

        public RelationshipResult(bool applied, string reason, EventId @event, IReadOnlyList<SocialDraw> draws)
        {
            Applied = applied;
            Reason = reason ?? string.Empty;
            Event = @event;
            Draws = draws ?? new List<SocialDraw>().AsReadOnly();
        }
    }

    public sealed class Relationships
    {
        public const long DayGameSeconds = 86400L;

        private const string ChatCode = "ACT-REL-01";
        private const string FriendlyCode = "ACT-REL-02";
        private const string RomanceCode = "ACT-REL-03";
        private const string ArgumentCode = "ACT-REL-04";
        private const string ChatKind = "chat";
        private const string FriendlyKind = "friendly";
        private const string RomanceKind = "romance";
        private const string RejectionKind = "rejection";
        private const string ArgumentKind = "argument";
        private const float ChatFriendshipGain = 3.0f;
        private const float ChatAngryFriendship = -2.0f;
        private const float ChatSocialGain = 10.0f;
        private const float FriendlyFriendshipGain = 6.0f;
        private const float FriendlySocialGain = 15.0f;
        private const float RomanceGain = 5.0f;
        private const float RomanceRejectionLoss = -2.0f;
        private const float ArgumentFriendshipLoss = -8.0f;
        private const float ArgumentRomanceLoss = -4.0f;

        private readonly WorldState world;

        public Relationships(WorldState world)
        {
            if (world == null)
            {
                throw new ArgumentNullException("world");
            }
            this.world = world;
        }

        public RelationshipResult Complete(StateWriter writer, ActivityId activity,
            ResidentId initiator, ResidentId recipient, double? acceptanceDraw = null,
            double? loserDraw = null, double? initiatorSentimentDraw = null,
            double? recipientSentimentDraw = null)
        {
            CheckWriter(writer);
            string code = activity.Code ?? string.Empty;
            if (!string.Equals(code, ChatCode, StringComparison.Ordinal)
                && !string.Equals(code, FriendlyCode, StringComparison.Ordinal)
                && !string.Equals(code, RomanceCode, StringComparison.Ordinal)
                && !string.Equals(code, ArgumentCode, StringComparison.Ordinal))
            {
                return Refuse("UnknownActivity " + code);
            }
            if (initiator == recipient)
            {
                return Refuse("SelfInteraction");
            }
            ResidentRecord first;
            ResidentRecord second;
            if (!world.TryGetResident(initiator, out first)
                || !world.TryGetResident(recipient, out second))
            {
                return Refuse("UnknownResident");
            }
            if (!ValidDraw(acceptanceDraw) || !ValidDraw(loserDraw)
                || !ValidDraw(initiatorSentimentDraw) || !ValidDraw(recipientSentimentDraw))
            {
                return Refuse("InvalidDraw");
            }
            if (string.Equals(code, ChatCode, StringComparison.Ordinal))
            {
                return CompleteChat(writer, first, second);
            }
            if (string.Equals(code, FriendlyCode, StringComparison.Ordinal))
            {
                return CompleteFriendly(writer, first, second);
            }
            if (string.Equals(code, RomanceCode, StringComparison.Ordinal))
            {
                return CompleteRomance(writer, first, second, acceptanceDraw,
                    initiatorSentimentDraw, recipientSentimentDraw);
            }
            return CompleteArgument(writer, first, second, loserDraw,
                initiatorSentimentDraw, recipientSentimentDraw);
        }

        public RelationshipResult Remember(StateWriter writer, ResidentId resident,
            EventId sourceEvent, EventId? learnedByChat = null)
        {
            CheckWriter(writer);
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record))
            {
                return Refuse("UnknownResident");
            }
            WorldEvent source = FindEvent(sourceEvent);
            if (source == null)
            {
                return Refuse("UnknownEvent");
            }
            if (FindMemory(record, sourceEvent) != null)
            {
                return Refuse("AlreadyKnown");
            }
            if (!learnedByChat.HasValue)
            {
                MemoryEntry evidence = FindParticipation(resident, sourceEvent);
                if (evidence == null)
                {
                    return Refuse("NotParticipant");
                }
                world.AddMemory(writer, resident, new MemoryEntry(sourceEvent, evidence.Kind,
                    evidence.GameSeconds, record.Home, CopyParticipants(evidence.Participants),
                    evidence.OutcomeCode, "participant"));
                return new RelationshipResult(true, string.Empty, sourceEvent, EmptyDraws());
            }
            WorldEvent chat = FindEvent(learnedByChat.Value);
            if (chat == null || !string.Equals(chat.Kind, ChatKind, StringComparison.Ordinal))
            {
                return Refuse("NotChatEdge");
            }
            if (!chat.Actor.HasValue)
            {
                return Refuse("ChatHasNoSpeaker");
            }
            ResidentRecord speaker;
            if (!world.TryGetResident(chat.Actor.Value, out speaker))
            {
                return Refuse("UnknownSpeaker");
            }
            MemoryEntry known = FindMemory(speaker, sourceEvent);
            if (known == null || !(LearnedAt(known) < chat.GameSeconds)
                || chat.Detail != ChatCode + " listener=" + ((long)resident).ToString())
            {
                return Refuse("SpeakerDidNotKnowEventBeforeChat");
            }
            world.AddMemory(writer, resident, new MemoryEntry(sourceEvent, known.Kind,
                known.GameSeconds, known.Place, CopyParticipants(known.Participants),
                known.OutcomeCode, "told-by:" + ((long)chat.Event).ToString()));
            return new RelationshipResult(true, string.Empty, sourceEvent, EmptyDraws());
        }

        public MemoryEntry Explain(ResidentId resident, EventId sourceEvent)
        {
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record))
            {
                return null;
            }
            MemoryEntry memory = FindMemory(record, sourceEvent);
            if (memory == null)
            {
                return null;
            }
            return new MemoryEntry(memory.Event, memory.Kind, memory.GameSeconds,
                memory.Place, CopyParticipants(memory.Participants),
                memory.OutcomeCode, memory.LearnedBy);
        }

        public void Tick(StateWriter writer)
        {
            if (writer != StateWriter.SimCore && writer != StateWriter.CommandArbitrator
                && writer != StateWriter.ActivityExecutor)
            {
                throw new ArgumentException("Unknown state writer", "writer");
            }
            long now = world.GameSeconds;
            ResidentRecord[] residents = world.ResidentsSnapshot();
            for (int i = 0; i < residents.Length; i++)
            {
                residents[i].FeelingList.RemoveAll(delegate(Feeling feeling)
                {
                    return now >= feeling.ExpiryGameSeconds;
                });
            }
        }

        private RelationshipResult CompleteChat(StateWriter writer,
            ResidentRecord initiator, ResidentRecord recipient)
        {
            float delta = HasActiveAngry(initiator, recipient.Id)
                || HasActiveAngry(recipient, initiator.Id)
                ? ChatAngryFriendship : ChatFriendshipGain;
            float initiatorFriendship;
            float initiatorRomance;
            BondToward(initiator, recipient.Id, out initiatorFriendship, out initiatorRomance);
            float recipientFriendship;
            float recipientRomance;
            BondToward(recipient, initiator.Id, out recipientFriendship, out recipientRomance);
            float initiatorSocial = initiator.GetNeed(Need.Social);
            float recipientSocial = recipient.GetNeed(Need.Social);
            world.SetBond(writer, initiator.Id, recipient.Id,
                initiatorFriendship + delta, initiatorRomance);
            world.SetBond(writer, recipient.Id, initiator.Id,
                recipientFriendship + delta, recipientRomance);
            world.SetNeed(writer, initiator.Id, Need.Social, initiatorSocial + ChatSocialGain);
            world.SetNeed(writer, recipient.Id, Need.Social, recipientSocial + ChatSocialGain);
            WorldEvent chat = world.AppendEvent(writer, ChatKind, initiator.Id, ChatCode + " listener=" + ((long)recipient.Id).ToString());
            RelayLatest(initiator, recipient, writer, chat);
            return new RelationshipResult(true, string.Empty, chat.Event, EmptyDraws());
        }

        // SDoc ACT-REL-02 FriendlyActivity: Friendship +6 pairwise, Social +15 (the optional Grateful is not drawn).
        private RelationshipResult CompleteFriendly(StateWriter writer,
            ResidentRecord initiator, ResidentRecord recipient)
        {
            float friendship;
            float romance;
            BondToward(initiator, recipient.Id, out friendship, out romance);
            world.SetBond(writer, initiator.Id, recipient.Id, friendship + FriendlyFriendshipGain, romance);
            BondToward(recipient, initiator.Id, out friendship, out romance);
            world.SetBond(writer, recipient.Id, initiator.Id, friendship + FriendlyFriendshipGain, romance);
            world.SetNeed(writer, initiator.Id, Need.Social, initiator.GetNeed(Need.Social) + FriendlySocialGain);
            world.SetNeed(writer, recipient.Id, Need.Social, recipient.GetNeed(Need.Social) + FriendlySocialGain);
            WorldEvent shared = world.AppendEvent(writer, FriendlyKind, initiator.Id,
                FriendlyCode + " partner=" + ((long)recipient.Id).ToString());
            return new RelationshipResult(true, string.Empty, shared.Event, EmptyDraws());
        }

        private RelationshipResult CompleteRomance(StateWriter writer,
            ResidentRecord initiator, ResidentRecord recipient, double? acceptanceDraw,
            double? initiatorSentimentDraw, double? recipientSentimentDraw)
        {
            float friendship;
            float romance;
            BondToward(recipient, initiator.Id, out friendship, out romance);
            double probability = Autonomy.AcceptanceProbability(friendship, romance,
                recipient.GetTrait(TraitAxis.Sociable),
                ActiveNegativeIntensity(recipient, initiator.Id));
            double draw = acceptanceDraw.HasValue ? acceptanceDraw.Value : world.NextRelationshipRandom(writer);
            bool accepted = draw < probability;
            List<SocialDraw> draws = new List<SocialDraw>();
            draws.Add(new SocialDraw(world.NextRelationshipSequence(writer), world.GameSeconds, "Acceptance",
                recipient.Id, initiator.Id, probability, draw, accepted));
            if (accepted)
            {
                float initiatorFriendship;
                float initiatorRomance;
                BondToward(initiator, recipient.Id, out initiatorFriendship, out initiatorRomance);
                world.SetBond(writer, initiator.Id, recipient.Id,
                    initiatorFriendship, initiatorRomance + RomanceGain);
                world.SetBond(writer, recipient.Id, initiator.Id,
                    friendship, romance + RomanceGain);
                WorldEvent loved = world.AppendEvent(writer, RomanceKind, initiator.Id,
                    RomanceCode + " accepted");
                return new RelationshipResult(true, string.Empty, loved.Event,
                    draws.AsReadOnly());
            }
            double initiatorProbability = SentimentFor(initiator, recipient.Id);
            double recipientProbability = SentimentFor(recipient, initiator.Id);
            float initiatorFriendshipBefore;
            float initiatorRomanceBefore;
            BondToward(initiator, recipient.Id, out initiatorFriendshipBefore, out initiatorRomanceBefore);
            world.SetBond(writer, initiator.Id, recipient.Id,
                initiatorFriendshipBefore, initiatorRomanceBefore + RomanceRejectionLoss);
            world.SetBond(writer, recipient.Id, initiator.Id,
                friendship, romance + RomanceRejectionLoss);
            WorldEvent rejection = world.AppendEvent(writer, RejectionKind, initiator.Id,
                RomanceCode + " rejected");
            AddParticipantMemory(writer, initiator, recipient, rejection, "rejected");
            AddParticipantMemory(writer, recipient, initiator, rejection, "rejected");
            ResolveSentiment(writer, draws, initiator, recipient.Id, rejection.Event,
                FeelingKind.Embarrassed, 1, initiatorProbability, initiatorSentimentDraw);
            ResolveSentiment(writer, draws, recipient, initiator.Id, rejection.Event,
                FeelingKind.Embarrassed, 1, recipientProbability, recipientSentimentDraw);
            return new RelationshipResult(true, string.Empty, rejection.Event,
                draws.AsReadOnly());
        }

        private RelationshipResult CompleteArgument(StateWriter writer,
            ResidentRecord initiator, ResidentRecord recipient, double? loserDraw,
            double? initiatorSentimentDraw, double? recipientSentimentDraw)
        {
            double loserProbability = Clamp(0.5
                + ((double)recipient.GetNeed(Need.Comfort) - (double)initiator.GetNeed(Need.Comfort)) / 200.0,
                0.05, 0.95);
            double loser = loserDraw.HasValue ? loserDraw.Value : world.NextRelationshipRandom(writer);
            bool initiatorLoses = loser < loserProbability;
            ResidentRecord eligible = initiatorLoses ? initiator : recipient;
            ResidentRecord other = initiatorLoses ? recipient : initiator;
            double sentimentProbability = SentimentFor(eligible, other.Id);
            double? sentimentProvided = initiatorLoses
                ? initiatorSentimentDraw : recipientSentimentDraw;
            double sentiment = sentimentProvided.HasValue
                ? sentimentProvided.Value : world.NextRelationshipRandom(writer);
            bool hurt = sentiment < sentimentProbability;
            List<SocialDraw> draws = new List<SocialDraw>();
            draws.Add(new SocialDraw(world.NextRelationshipSequence(writer), world.GameSeconds, "Loser",
                initiator.Id, recipient.Id, loserProbability, loser, initiatorLoses));
            draws.Add(new SocialDraw(world.NextRelationshipSequence(writer), world.GameSeconds, "Sentiment",
                eligible.Id, other.Id, sentimentProbability, sentiment, hurt));
            float initiatorFriendship;
            float initiatorRomance;
            BondToward(initiator, recipient.Id, out initiatorFriendship, out initiatorRomance);
            float recipientFriendship;
            float recipientRomance;
            BondToward(recipient, initiator.Id, out recipientFriendship, out recipientRomance);
            world.SetBond(writer, initiator.Id, recipient.Id,
                initiatorFriendship + ArgumentFriendshipLoss,
                initiatorRomance != 0.0f ? initiatorRomance + ArgumentRomanceLoss : initiatorRomance);
            world.SetBond(writer, recipient.Id, initiator.Id,
                recipientFriendship + ArgumentFriendshipLoss,
                recipientRomance != 0.0f ? recipientRomance + ArgumentRomanceLoss : recipientRomance);
            WorldEvent argument = world.AppendEvent(writer, ArgumentKind, initiator.Id, ArgumentCode);
            AddParticipantMemory(writer, initiator, recipient, argument, "argued");
            AddParticipantMemory(writer, recipient, initiator, argument, "argued");
            if (hurt)
            {
                world.AddFeeling(writer, eligible.Id, FeelingKind.Hurt, argument.Event,
                    other.Id, 2, world.GameSeconds + DurationFor(FeelingKind.Hurt, 2));
            }
            return new RelationshipResult(true, string.Empty, argument.Event,
                draws.AsReadOnly());
        }

        private void RelayLatest(ResidentRecord speaker, ResidentRecord listener,
            StateWriter writer, WorldEvent chat)
        {
            MemoryEntry latest = null;
            for (int i = 0; i < speaker.Memories.Count; i++)
            {
                MemoryEntry candidate = speaker.Memories[i];
                if (!((long)candidate.Event < (long)chat.Event) || LearnedAt(candidate) >= chat.GameSeconds)
                {
                    continue;
                }
                if (latest == null || candidate.GameSeconds > latest.GameSeconds
                    || (candidate.GameSeconds == latest.GameSeconds
                        && (long)candidate.Event > (long)latest.Event))
                {
                    latest = candidate;
                }
            }
            if (latest == null || FindMemory(listener, latest.Event) != null)
            {
                return;
            }
            world.AddMemory(writer, listener.Id, new MemoryEntry(latest.Event, latest.Kind,
                latest.GameSeconds, latest.Place, CopyParticipants(latest.Participants),
                latest.OutcomeCode, "told-by:" + ((long)chat.Event).ToString()));
        }

        private void ResolveSentiment(StateWriter writer, List<SocialDraw> draws,
            ResidentRecord resident, ResidentId other, EventId source, FeelingKind kind,
            int intensity, double probability, double? provided)
        {
            double draw = provided.HasValue ? provided.Value : world.NextRelationshipRandom(writer);
            bool assigned = draw < probability;
            draws.Add(new SocialDraw(world.NextRelationshipSequence(writer), world.GameSeconds, "Sentiment",
                resident.Id, other, probability, draw, assigned));
            if (assigned)
            {
                world.AddFeeling(writer, resident.Id, kind, source, other, intensity,
                    world.GameSeconds + DurationFor(kind, intensity));
            }
        }

        private void AddParticipantMemory(StateWriter writer, ResidentRecord resident,
            ResidentRecord partner, WorldEvent source, string outcome)
        {
            List<ResidentId> participants = new List<ResidentId>();
            participants.Add(resident.Id);
            participants.Add(partner.Id);
            world.AddMemory(writer, resident.Id, new MemoryEntry(source.Event, source.Kind,
                source.GameSeconds, resident.Home, participants.AsReadOnly(),
                outcome, "participant"));
        }

        private long LearnedAt(MemoryEntry memory)
        {
            const string prefix = "told-by:";
            if (!memory.LearnedBy.StartsWith(prefix, StringComparison.Ordinal)) return memory.GameSeconds;
            long id;
            if (!long.TryParse(memory.LearnedBy.Substring(prefix.Length), out id)) return long.MaxValue;
            WorldEvent chat = FindEvent((EventId)id);
            return chat == null ? long.MaxValue : chat.GameSeconds;
        }

        private static void CheckWriter(StateWriter writer)
        {
            if (writer != StateWriter.SimCore && writer != StateWriter.CommandArbitrator && writer != StateWriter.ActivityExecutor)
                throw new ArgumentException("Unknown state writer", "writer");
        }

        private double SentimentFor(ResidentRecord resident, ResidentId other)
        {
            float friendship;
            float romance;
            BondToward(resident, other, out friendship, out romance);
            return Autonomy.SentimentProbability(resident.GetTrait(TraitAxis.Sociable),
                resident.GetNeed(Need.Social), friendship);
        }

        private int ActiveNegativeIntensity(ResidentRecord resident, ResidentId other)
        {
            int total = 0;
            for (int i = 0; i < resident.Feelings.Count; i++)
            {
                Feeling feeling = resident.Feelings[i];
                if (feeling.Other == other && world.GameSeconds < feeling.ExpiryGameSeconds
                    && !IsPositiveFeeling(feeling.Kind))
                {
                    total += feeling.Intensity;
                }
            }
            return total;
        }

        private bool HasActiveAngry(ResidentRecord resident, ResidentId other)
        {
            for (int i = 0; i < resident.Feelings.Count; i++)
            {
                Feeling feeling = resident.Feelings[i];
                if (feeling.Kind == FeelingKind.Angry && feeling.Other == other
                    && world.GameSeconds < feeling.ExpiryGameSeconds)
                {
                    return true;
                }
            }
            return false;
        }

        private WorldEvent FindEvent(EventId id)
        {
            for (int i = 0; i < world.Events.Count; i++)
            {
                if (world.Events[i].Event == id)
                {
                    return world.Events[i];
                }
            }
            return null;
        }

        private MemoryEntry FindParticipation(ResidentId resident, EventId source)
        {
            ResidentRecord[] residents = world.ResidentsSnapshot();
            for (int i = 0; i < residents.Length; i++)
            {
                for (int m = 0; m < residents[i].Memories.Count; m++)
                {
                    MemoryEntry memory = residents[i].Memories[m];
                    if (memory.Event != source)
                    {
                        continue;
                    }
                    for (int p = 0; p < memory.Participants.Count; p++)
                    {
                        if (memory.Participants[p] == resident)
                        {
                            return memory;
                        }
                    }
                }
            }
            return null;
        }

        private static MemoryEntry FindMemory(ResidentRecord resident, EventId source)
        {
            for (int i = 0; i < resident.Memories.Count; i++)
            {
                if (resident.Memories[i].Event == source)
                {
                    return resident.Memories[i];
                }
            }
            return null;
        }

        private static void BondToward(ResidentRecord resident, ResidentId other,
            out float friendship, out float romance)
        {
            friendship = 0.0f;
            romance = 0.0f;
            for (int i = 0; i < resident.Bonds.Count; i++)
            {
                if (resident.Bonds[i].Other == other)
                {
                    friendship = resident.Bonds[i].Friendship;
                    romance = resident.Bonds[i].Romance;
                    return;
                }
            }
        }

        private static bool IsPositiveFeeling(FeelingKind kind)
        {
            return kind == FeelingKind.Grateful || kind == FeelingKind.Affectionate
                || kind == FeelingKind.Proud;
        }

        private static long DurationFor(FeelingKind kind, int intensity)
        {
            long scaled = DayGameSeconds * (long)intensity;
            if (kind == FeelingKind.Hurt || kind == FeelingKind.Affectionate)
            {
                return 2L * scaled;
            }
            if (kind == FeelingKind.Angry || kind == FeelingKind.Embarrassed)
            {
                return scaled / 2L;
            }
            return scaled;
        }

        private static IReadOnlyList<ResidentId> CopyParticipants(
            IReadOnlyList<ResidentId> participants)
        {
            List<ResidentId> copy = new List<ResidentId>();
            for (int i = 0; i < participants.Count; i++)
            {
                copy.Add(participants[i]);
            }
            return copy.AsReadOnly();
        }

        private static bool ValidDraw(double? draw)
        {
            return !draw.HasValue || (draw.Value >= 0.0 && draw.Value < 1.0);
        }

        private static double Clamp(double value, double low, double high)
        {
            if (value < low)
            {
                return low;
            }
            if (value > high)
            {
                return high;
            }
            return value;
        }

        private static RelationshipResult Refuse(string reason)
        {
            return new RelationshipResult(false, reason, default(EventId), EmptyDraws());
        }

        private static IReadOnlyList<SocialDraw> EmptyDraws()
        {
            return new List<SocialDraw>().AsReadOnly();
        }
    }
}
