// CLUB@1: club meetings and community help (BP-ACT-10, BP-SEC-05).
// Consumes STATE@1 (WorldState needs/bonds/feelings/memories/events) and
// RELATION@1 (RelationshipResult, SocialDraw, Chat memory relay) plus the
// PERSONALITY@1 sentiment probability. Plain C# 9, no engine dependency.
// Post-completion boundary like RELATION.Complete: meetings grant standing,
// Social and pairwise friendship to member attendees only; community help
// transitions a registered target one step to Ready and records factual
// participant memories; gratitude is evaluated once per resident/help event,
// only for residents holding a help memory (participation or real Chat
// learning). No static mutable state; all writes flow through audited
// WorldState methods with the caller StateWriter.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;
using Blockparty.Simulation;

namespace Blockparty.Activities
{
    // Save capture bundle: memberships, standings, help targets, help-event
    // helpers and evaluated gratitude pairs. No action ids are stored, so
    // SAVE restores this section without a remap.
    public sealed class ClubSnapshot
    {
        public sealed class MemberRow
        {
            public string Club;
            public long Resident;
        }

        public sealed class StandingRow
        {
            public string Club;
            public long Resident;
            public int Count;
        }

        public sealed class TargetRow
        {
            public long Target;
            public long Household;
            public string State;
        }

        public sealed class HelpRow
        {
            public long Event;
            public long Helper;
            public long Target;
        }

        public sealed class EvaluatedRow
        {
            public long Resident;
            public long Event;
        }

        public readonly List<MemberRow> Members = new List<MemberRow>();
        public readonly List<StandingRow> Standings = new List<StandingRow>();
        public readonly List<TargetRow> Targets = new List<TargetRow>();
        public readonly List<HelpRow> Helps = new List<HelpRow>();
        public readonly List<EvaluatedRow> Evaluated = new List<EvaluatedRow>();
    }

    public sealed class Club
    {
        private const float MeetingSocialGain = 20.0f;
        private const float MeetingFriendshipGain = 3.0f;
        private const int GratefulIntensity = 1;
        private const long DayGameSeconds = 86400L;
        private const string HelpKind = "help";
        private const string HelpedOutcome = "helped";
        private const string ParticipantLearnedBy = "participant";
        private const string ReadyState = "Ready";

        private sealed class HelpTarget
        {
            public HouseholdId Household;
            public string State;
        }

        private sealed class HelpRecord
        {
            public ResidentId Helper;
            public ObjectId Target;
        }

        private readonly WorldState world;
        private readonly Dictionary<string, Dictionary<ResidentId, bool>> members =
            new Dictionary<string, Dictionary<ResidentId, bool>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<ResidentId, int>> standings =
            new Dictionary<string, Dictionary<ResidentId, int>>(StringComparer.Ordinal);
        private readonly Dictionary<ObjectId, HelpTarget> targets = new Dictionary<ObjectId, HelpTarget>();
        private readonly Dictionary<EventId, HelpRecord> helps = new Dictionary<EventId, HelpRecord>();
        private readonly Dictionary<ResidentId, Dictionary<EventId, bool>> evaluated =
            new Dictionary<ResidentId, Dictionary<EventId, bool>>();

        public Club(WorldState world)
        {
            if (world == null)
            {
                throw new ArgumentNullException("world");
            }
            this.world = world;
        }

        public void SetMembership(StateWriter writer, string club, ResidentId member, bool joined)
        {
            CheckWriter(writer);
            ResidentRecord record;
            if (!world.TryGetResident(member, out record))
            {
                throw new ArgumentException("Unknown resident " + ((long)member).ToString(), "member");
            }
            string key = club ?? string.Empty;
            if (joined)
            {
                MembersFor(key)[member] = true;
                return;
            }
            Dictionary<ResidentId, bool> roster;
            if (members.TryGetValue(key, out roster))
            {
                roster.Remove(member);
            }
        }

        public int Standing(string club, ResidentId resident)
        {
            Dictionary<ResidentId, int> table;
            int count;
            if (standings.TryGetValue(club ?? string.Empty, out table)
                && table.TryGetValue(resident, out count))
            {
                return count;
            }
            return 0;
        }

        public RelationshipResult ClubMeeting(StateWriter writer, string club, ResidentId[] attendees)
        {
            CheckWriter(writer);
            if (attendees == null || attendees.Length == 0)
            {
                return Refuse("NoAttendees");
            }
            for (int i = 0; i < attendees.Length; i++)
            {
                ResidentRecord record;
                if (!world.TryGetResident(attendees[i], out record))
                {
                    return Refuse("UnknownResident");
                }
            }
            string key = club ?? string.Empty;
            List<ResidentId> present = new List<ResidentId>();
            for (int i = 0; i < attendees.Length; i++)
            {
                if (IsMember(key, attendees[i]) && !present.Contains(attendees[i]))
                {
                    present.Add(attendees[i]);
                }
            }
            Dictionary<ResidentId, int> table = StandingsFor(key);
            for (int i = 0; i < present.Count; i++)
            {
                int count;
                table.TryGetValue(present[i], out count);
                table[present[i]] = count + 1;
                ResidentRecord record;
                world.TryGetResident(present[i], out record);
                world.SetNeed(writer, present[i], Need.Social,
                    record.GetNeed(Need.Social) + MeetingSocialGain);
            }
            for (int i = 0; i < present.Count; i++)
            {
                for (int j = 0; j < present.Count; j++)
                {
                    if (i == j)
                    {
                        continue;
                    }
                    ResidentRecord record;
                    world.TryGetResident(present[i], out record);
                    float friendship;
                    float romance;
                    BondToward(record, present[j], out friendship, out romance);
                    world.SetBond(writer, present[i], present[j],
                        friendship + MeetingFriendshipGain, romance);
                }
            }
            return new RelationshipResult(true, string.Empty, default(EventId), EmptyDraws());
        }

        public void RegisterHelpTarget(StateWriter writer, ObjectId target, HouseholdId household, string state)
        {
            CheckWriter(writer);
            HelpTarget entry = new HelpTarget();
            entry.Household = household;
            entry.State = state ?? string.Empty;
            targets[target] = entry;
        }

        public string HelpTargetState(ObjectId target)
        {
            HelpTarget entry;
            if (targets.TryGetValue(target, out entry))
            {
                return entry.State;
            }
            return string.Empty;
        }

        public RelationshipResult CommunityHelp(StateWriter writer, ResidentId helper,
            ObjectId target, ResidentId[] participants)
        {
            CheckWriter(writer);
            if (participants == null || participants.Length == 0)
            {
                return Refuse("NoParticipants");
            }
            ResidentRecord record;
            if (!world.TryGetResident(helper, out record))
            {
                return Refuse("UnknownResident");
            }
            for (int i = 0; i < participants.Length; i++)
            {
                if (!world.TryGetResident(participants[i], out record))
                {
                    return Refuse("UnknownResident");
                }
            }
            HelpTarget entry;
            if (!targets.TryGetValue(target, out entry))
            {
                return Refuse("UnknownTarget");
            }
            entry.State = ReadyState;
            WorldEvent help = world.AppendEvent(writer, HelpKind, helper,
                "community-help target=" + ((long)target).ToString());
            HelpRecord stored = new HelpRecord();
            stored.Helper = helper;
            stored.Target = target;
            helps[help.Event] = stored;
            List<ResidentId> shared = new List<ResidentId>(participants);
            for (int i = 0; i < participants.Length; i++)
            {
                ResidentRecord participant;
                world.TryGetResident(participants[i], out participant);
                world.AddMemory(writer, participants[i], new MemoryEntry(help.Event, help.Kind,
                    help.GameSeconds, participant.Home, shared.AsReadOnly(),
                    HelpedOutcome, ParticipantLearnedBy));
            }
            return new RelationshipResult(true, string.Empty, help.Event, EmptyDraws());
        }

        public RelationshipResult ResolveGratitude(StateWriter writer, ResidentId resident,
            EventId helpEvent, double? sentimentDraw = null)
        {
            CheckWriter(writer);
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record))
            {
                return Refuse("UnknownResident");
            }
            if (sentimentDraw.HasValue && (sentimentDraw.Value < 0.0 || sentimentDraw.Value >= 1.0))
            {
                return Refuse("InvalidDraw");
            }
            HelpRecord help;
            if (!helps.TryGetValue(helpEvent, out help))
            {
                return Refuse("UnknownHelpEvent");
            }
            Dictionary<EventId, bool> done;
            if (evaluated.TryGetValue(resident, out done) && done.ContainsKey(helpEvent))
            {
                return Refuse("AlreadyEvaluated");
            }
            if (FindMemory(record, helpEvent) == null)
            {
                return Refuse("NotInformed");
            }
            float friendship;
            float romance;
            BondToward(record, help.Helper, out friendship, out romance);
            double probability = Autonomy.SentimentProbability(record.GetTrait(TraitAxis.Sociable),
                record.GetNeed(Need.Social), friendship);
            double draw = sentimentDraw.HasValue ? sentimentDraw.Value : world.NextRelationshipRandom(writer);
            bool accepted = draw < probability;
            List<SocialDraw> draws = new List<SocialDraw>();
            draws.Add(new SocialDraw(world.NextRelationshipSequence(writer), world.GameSeconds, "Sentiment",
                resident, help.Helper, probability, draw, accepted));
            EvaluatedFor(resident)[helpEvent] = true;
            if (accepted)
            {
                world.AddFeeling(writer, resident, FeelingKind.Grateful, helpEvent,
                    help.Helper, GratefulIntensity, world.GameSeconds + DayGameSeconds);
            }
            return new RelationshipResult(true, string.Empty, helpEvent, draws.AsReadOnly());
        }

        // Save capture: rows sorted (clubs ordinal, then ids) for stable bytes.
        public ClubSnapshot Export()
        {
            ClubSnapshot snapshot = new ClubSnapshot();
            List<string> clubs = new List<string>(members.Keys);
            SortClubs(clubs);
            for (int c = 0; c < clubs.Count; c++)
            {
                List<ResidentId> roster = new List<ResidentId>(members[clubs[c]].Keys);
                SortResidents(roster);
                for (int i = 0; i < roster.Count; i++)
                {
                    ClubSnapshot.MemberRow row = new ClubSnapshot.MemberRow();
                    row.Club = clubs[c];
                    row.Resident = (long)roster[i];
                    snapshot.Members.Add(row);
                }
            }
            List<string> tables = new List<string>(standings.Keys);
            SortClubs(tables);
            for (int c = 0; c < tables.Count; c++)
            {
                List<ResidentId> rows = new List<ResidentId>(standings[tables[c]].Keys);
                SortResidents(rows);
                for (int i = 0; i < rows.Count; i++)
                {
                    ClubSnapshot.StandingRow row = new ClubSnapshot.StandingRow();
                    row.Club = tables[c];
                    row.Resident = (long)rows[i];
                    row.Count = standings[tables[c]][rows[i]];
                    snapshot.Standings.Add(row);
                }
            }
            List<ObjectId> ids = new List<ObjectId>(targets.Keys);
            for (int i = 1; i < ids.Count; i++)
            {
                ObjectId key = ids[i];
                int j = i - 1;
                while (j >= 0 && (long)ids[j] > (long)key) { ids[j + 1] = ids[j]; j--; }
                ids[j + 1] = key;
            }
            for (int i = 0; i < ids.Count; i++)
            {
                ClubSnapshot.TargetRow row = new ClubSnapshot.TargetRow();
                row.Target = (long)ids[i];
                row.Household = (long)targets[ids[i]].Household;
                row.State = targets[ids[i]].State;
                snapshot.Targets.Add(row);
            }
            List<EventId> events = new List<EventId>(helps.Keys);
            for (int i = 1; i < events.Count; i++)
            {
                EventId key = events[i];
                int j = i - 1;
                while (j >= 0 && (long)events[j] > (long)key) { events[j + 1] = events[j]; j--; }
                events[j + 1] = key;
            }
            for (int i = 0; i < events.Count; i++)
            {
                ClubSnapshot.HelpRow row = new ClubSnapshot.HelpRow();
                row.Event = (long)events[i];
                row.Helper = (long)helps[events[i]].Helper;
                row.Target = (long)helps[events[i]].Target;
                snapshot.Helps.Add(row);
            }
            List<ResidentId> residents = new List<ResidentId>(evaluated.Keys);
            SortResidents(residents);
            for (int i = 0; i < residents.Count; i++)
            {
                List<EventId> done = new List<EventId>(evaluated[residents[i]].Keys);
                for (int e = 1; e < done.Count; e++)
                {
                    EventId key = done[e];
                    int j = e - 1;
                    while (j >= 0 && (long)done[j] > (long)key) { done[j + 1] = done[j]; j--; }
                    done[j + 1] = key;
                }
                for (int e = 0; e < done.Count; e++)
                {
                    ClubSnapshot.EvaluatedRow row = new ClubSnapshot.EvaluatedRow();
                    row.Resident = (long)residents[i];
                    row.Event = (long)done[e];
                    snapshot.Evaluated.Add(row);
                }
            }
            return snapshot;
        }

        // Save restore: replaces memberships, standings, targets, helps and
        // evaluated pairs after validating every row, so a forged bundle is
        // refused instead of half-applied.
        public void Import(StateWriter writer, ClubSnapshot snapshot)
        {
            CheckWriter(writer);
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }
            if (snapshot.Members == null || snapshot.Standings == null || snapshot.Targets == null
                || snapshot.Helps == null || snapshot.Evaluated == null)
            {
                throw new ArgumentException("Club bundle lists must exist", "snapshot");
            }
            Dictionary<string, Dictionary<ResidentId, bool>> nextMembers =
                new Dictionary<string, Dictionary<ResidentId, bool>>(StringComparer.Ordinal);
            for (int i = 0; i < snapshot.Members.Count; i++)
            {
                ClubSnapshot.MemberRow row = snapshot.Members[i];
                if (row == null || row.Club == null)
                {
                    throw new ArgumentException("Null club member row " + i.ToString(), "snapshot");
                }
                ResidentId resident = (ResidentId)row.Resident;
                Dictionary<ResidentId, bool> roster;
                if (!nextMembers.TryGetValue(row.Club, out roster))
                {
                    roster = new Dictionary<ResidentId, bool>();
                    nextMembers[row.Club] = roster;
                }
                if (roster.ContainsKey(resident))
                {
                    throw new ArgumentException("Duplicate club member " + row.Resident.ToString(), "snapshot");
                }
                roster[resident] = true;
            }
            Dictionary<string, Dictionary<ResidentId, int>> nextStandings =
                new Dictionary<string, Dictionary<ResidentId, int>>(StringComparer.Ordinal);
            for (int i = 0; i < snapshot.Standings.Count; i++)
            {
                ClubSnapshot.StandingRow row = snapshot.Standings[i];
                if (row == null || row.Club == null || row.Count < 0)
                {
                    throw new ArgumentException("Null club standing row " + i.ToString(), "snapshot");
                }
                ResidentId resident = (ResidentId)row.Resident;
                Dictionary<ResidentId, int> table;
                if (!nextStandings.TryGetValue(row.Club, out table))
                {
                    table = new Dictionary<ResidentId, int>();
                    nextStandings[row.Club] = table;
                }
                if (table.ContainsKey(resident))
                {
                    throw new ArgumentException("Duplicate club standing " + row.Resident.ToString(), "snapshot");
                }
                table[resident] = row.Count;
            }
            Dictionary<ObjectId, HelpTarget> nextTargets = new Dictionary<ObjectId, HelpTarget>();
            for (int i = 0; i < snapshot.Targets.Count; i++)
            {
                ClubSnapshot.TargetRow row = snapshot.Targets[i];
                if (row == null || row.State == null)
                {
                    throw new ArgumentException("Null club target row " + i.ToString(), "snapshot");
                }
                ObjectId target = (ObjectId)row.Target;
                if (nextTargets.ContainsKey(target))
                {
                    throw new ArgumentException("Duplicate club target " + row.Target.ToString(), "snapshot");
                }
                HelpTarget entry = new HelpTarget();
                entry.Household = (HouseholdId)row.Household;
                entry.State = row.State;
                nextTargets[target] = entry;
            }
            Dictionary<EventId, HelpRecord> nextHelps = new Dictionary<EventId, HelpRecord>();
            for (int i = 0; i < snapshot.Helps.Count; i++)
            {
                ClubSnapshot.HelpRow row = snapshot.Helps[i];
                if (row == null)
                {
                    throw new ArgumentException("Null club help row " + i.ToString(), "snapshot");
                }
                EventId id = (EventId)row.Event;
                if (nextHelps.ContainsKey(id))
                {
                    throw new ArgumentException("Duplicate club help " + row.Event.ToString(), "snapshot");
                }
                HelpRecord stored = new HelpRecord();
                stored.Helper = (ResidentId)row.Helper;
                stored.Target = (ObjectId)row.Target;
                nextHelps[id] = stored;
            }
            Dictionary<ResidentId, Dictionary<EventId, bool>> nextEvaluated =
                new Dictionary<ResidentId, Dictionary<EventId, bool>>();
            for (int i = 0; i < snapshot.Evaluated.Count; i++)
            {
                ClubSnapshot.EvaluatedRow row = snapshot.Evaluated[i];
                if (row == null)
                {
                    throw new ArgumentException("Null club evaluated row " + i.ToString(), "snapshot");
                }
                ResidentId resident = (ResidentId)row.Resident;
                EventId id = (EventId)row.Event;
                Dictionary<EventId, bool> done;
                if (!nextEvaluated.TryGetValue(resident, out done))
                {
                    done = new Dictionary<EventId, bool>();
                    nextEvaluated[resident] = done;
                }
                if (done.ContainsKey(id))
                {
                    throw new ArgumentException("Duplicate club evaluation " + row.Event.ToString(), "snapshot");
                }
                done[id] = true;
            }
            members.Clear();
            foreach (KeyValuePair<string, Dictionary<ResidentId, bool>> pair in nextMembers)
            {
                members[pair.Key] = pair.Value;
            }
            standings.Clear();
            foreach (KeyValuePair<string, Dictionary<ResidentId, int>> pair in nextStandings)
            {
                standings[pair.Key] = pair.Value;
            }
            targets.Clear();
            foreach (KeyValuePair<ObjectId, HelpTarget> pair in nextTargets)
            {
                targets[pair.Key] = pair.Value;
            }
            helps.Clear();
            foreach (KeyValuePair<EventId, HelpRecord> pair in nextHelps)
            {
                helps[pair.Key] = pair.Value;
            }
            evaluated.Clear();
            foreach (KeyValuePair<ResidentId, Dictionary<EventId, bool>> pair in nextEvaluated)
            {
                evaluated[pair.Key] = pair.Value;
            }
        }

        internal bool HasSaveState()
        {
            return members.Count != 0 || standings.Count != 0 || targets.Count != 0
                || helps.Count != 0 || evaluated.Count != 0;
        }

        private Dictionary<ResidentId, bool> MembersFor(string club)
        {
            Dictionary<ResidentId, bool> roster;
            if (!members.TryGetValue(club, out roster))
            {
                roster = new Dictionary<ResidentId, bool>();
                members[club] = roster;
            }
            return roster;
        }

        private Dictionary<ResidentId, int> StandingsFor(string club)
        {
            Dictionary<ResidentId, int> table;
            if (!standings.TryGetValue(club, out table))
            {
                table = new Dictionary<ResidentId, int>();
                standings[club] = table;
            }
            return table;
        }

        private Dictionary<EventId, bool> EvaluatedFor(ResidentId resident)
        {
            Dictionary<EventId, bool> done;
            if (!evaluated.TryGetValue(resident, out done))
            {
                done = new Dictionary<EventId, bool>();
                evaluated[resident] = done;
            }
            return done;
        }

        private bool IsMember(string club, ResidentId resident)
        {
            Dictionary<ResidentId, bool> roster;
            return members.TryGetValue(club, out roster) && roster.ContainsKey(resident);
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

        private static void SortClubs(List<string> clubs)
        {
            for (int i = 1; i < clubs.Count; i++)
            {
                string key = clubs[i];
                int j = i - 1;
                while (j >= 0 && string.Compare(clubs[j], key, StringComparison.Ordinal) > 0)
                {
                    clubs[j + 1] = clubs[j];
                    j--;
                }
                clubs[j + 1] = key;
            }
        }

        private static void SortResidents(List<ResidentId> rows)
        {
            for (int i = 1; i < rows.Count; i++)
            {
                ResidentId key = rows[i];
                int j = i - 1;
                while (j >= 0 && (long)rows[j] > (long)key) { rows[j + 1] = rows[j]; j--; }
                rows[j + 1] = key;
            }
        }

        private static void CheckWriter(StateWriter writer)
        {
            if (writer != StateWriter.SimCore && writer != StateWriter.CommandArbitrator && writer != StateWriter.ActivityExecutor)
                throw new ArgumentException("Unknown state writer", "writer");
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
