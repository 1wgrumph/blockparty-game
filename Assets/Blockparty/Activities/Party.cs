// PARTY@1: party attendance, actual chats and host terminals (BP-ACT-07,
// ACT-PARTY-01). Follows the SOCIAL@1/MINIGOLF@1 service shape: Party(
// WorldState, CommandArbitrator, ActivityExecutor, Accounts). Tick adopts the
// host's real NAV action, charges the 30-unit event cost once at perform entry
// and closes the party from the executor's factual terminal. Enter/Exit are
// the accepted invitation's arrival and departure boundaries, keyed by the
// host ActionInstanceId; that id stays usable across load even though SAVE
// remaps the live NAV action. Attendance effects are proportional to each
// recorded interval and commit once per attendee through the keyed effect
// ledger. The +4 pair gain comes only from a completed Chat event inside both
// recorded intervals; Grateful and Proud read the same recorded times and
// stored draws. Plain C# 9, no engine dependency.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;
using Blockparty.Simulation;

namespace Blockparty.Activities
{
    // Save capture bundle: per-party row keyed by the caller's stable id plus
    // the live NAV action id, the charged/done markers, and each attendee's
    // interval and stored sentiment draw. Export sorts rows for stable bytes.
    public sealed class PartySnapshot
    {
        public sealed class AttendeeRow
        {
            public long Resident;
            public long Entry;
            public long Exit = -1L;
            public bool HasDraw;
            public double Draw;
        }

        public sealed class PartyRow
        {
            public long Key;
            public long Action;
            public long Host;
            public long Start = -1L;
            public bool Charged;
            public bool Done;
            public readonly List<AttendeeRow> Attendees = new List<AttendeeRow>();
        }

        public readonly List<PartyRow> Rows = new List<PartyRow>();
        public readonly List<PartyPlan> Plans = new List<PartyPlan>();
    }

    // PARTY-LIVE (M5-OD-50/51): one dated party plan and its invitations. The
    // live composition (LiveSimulation) drives it; Party owns it so SAVE keeps
    // planned parties, answers and countdowns. Times are game seconds.
    public sealed class PartyInvite
    {
        public long Guest;
        public bool Accepted;
        public bool Sent;      // travel submitted at start minus 15 game minutes
        public bool Arrived;   // Party.Enter recorded
        public bool Chatted;   // in one scheduled Chat pair
        public bool NoShow;

        public PartyInvite Copy()
        {
            return new PartyInvite { Guest = Guest, Accepted = Accepted, Sent = Sent,
                Arrived = Arrived, Chatted = Chatted, NoShow = NoShow };
        }
    }

    public sealed class PartyPlan
    {
        public long Id;
        public long Host;
        public long Created;
        public long Start;
        public long Venue;       // 0: the host's own spot
        public bool TravelStarted; // guests (and a venue host) set off at start minus 15 game minutes
        public long HostAction;  // 0 until the host's ACT-PARTY-01 is submitted at the start
        public bool Announced;   // start ping sent
        public bool Closed;
        public readonly List<PartyInvite> Invites = new List<PartyInvite>();

        public PartyPlan Copy()
        {
            PartyPlan copy = new PartyPlan { Id = Id, Host = Host, Created = Created, Start = Start,
                Venue = Venue, TravelStarted = TravelStarted, HostAction = HostAction, Announced = Announced, Closed = Closed };
            for (int i = 0; i < Invites.Count; i++) copy.Invites.Add(Invites[i].Copy());
            return copy;
        }
    }

    public sealed class Party
    {
        public const string ActivityCode = "ACT-PARTY-01";
        public const long DurationGameSeconds = 180L * 60L;
        public const long EligibleGameSeconds = 120L * 60L;
        public const long HostCost = 30L;
        public const float SocialGain = 30.0f;
        public const float FunGain = 20.0f;
        public const float EnergyGain = -15.0f;
        public const float PairFriendshipGain = 4.0f;
        public const int HostProudAttendees = 3;

        private const string ChatKind = "chat";
        private const string ChatDetailPrefix = "ACT-REL-01 listener=";
        private const string AttendanceEffectKind = "party";
        private const string PairEffectKind = "party-pair";
        private const string EventKind = "party";
        private const string ExpenseAccount = "PartyFoodAndDrink";
        private const string AttendedOutcome = "attended";
        private const string ParticipantLearnedBy = "participant";
        private const int SentimentIntensity = 1;
        private const long GameDaySeconds = 86400L;

        private sealed class Attendance
        {
            public ResidentId Resident;
            public long Entry;
            public long Exit = -1L;
            public bool HasDraw;
            public double Draw;
        }

        private sealed class Tracked
        {
            // Key is the caller-facing id; Action is the live NAV id SAVE may remap.
            public ActionInstanceId Key;
            public ActionInstanceId Action;
            public ResidentId Host;
            public long Start = -1L;
            public bool Charged;
            public bool Done;
            public readonly List<Attendance> Attendees = new List<Attendance>();
        }

        private readonly WorldState world;
        private readonly CommandArbitrator arbitrator;
        private readonly ActivityExecutor executor;
        private readonly Accounts accounts;
        private readonly List<Tracked> parties = new List<Tracked>();
        private readonly List<PartyPlan> plans = new List<PartyPlan>();
        private readonly Dictionary<ActionInstanceId, Tracked> byKey =
            new Dictionary<ActionInstanceId, Tracked>();
        private readonly Dictionary<ActionInstanceId, Tracked> byAction =
            new Dictionary<ActionInstanceId, Tracked>();

        public Party(WorldState world, CommandArbitrator arbitrator,
            ActivityExecutor executor, Accounts accounts)
        {
            if (world == null) throw new ArgumentNullException("world");
            if (arbitrator == null) throw new ArgumentNullException("arbitrator");
            if (executor == null) throw new ArgumentNullException("executor");
            this.world = world;
            this.arbitrator = arbitrator;
            this.executor = executor;
            this.accounts = accounts ?? new Accounts(world);
            executor.PartyService = this;
        }

        public static Party FromExecutor(ActivityExecutor executor)
        {
            if (executor == null) throw new ArgumentNullException("executor");
            return executor.PartyService;
        }

        // One fixed-tick boundary call after the executor Tick: adopt the
        // host's current party action, charge at perform entry, and close a
        // party on its factual terminal.
        public void Tick(StateWriter writer)
        {
            CheckWriter(writer);
            Sweep();
            for (int i = 0; i < parties.Count; i++)
            {
                Observe(writer, parties[i]);
            }
        }

        // The accepted invitation's actual arrival boundary (never a completion).
        public void Enter(StateWriter writer, ActionInstanceId party, ResidentId attendee,
            double? sentimentDraw = null)
        {
            CheckWriter(writer);
            if (sentimentDraw.HasValue && (sentimentDraw.Value < 0.0 || sentimentDraw.Value >= 1.0))
            {
                throw new ArgumentOutOfRangeException("sentimentDraw",
                    "Sentiment draw must be in [0, 1)");
            }
            ResidentRecord resident;
            if (!world.TryGetResident(attendee, out resident) || !resident.IsAlive)
            {
                throw new ArgumentException("Unknown resident " + ((long)attendee).ToString(),
                    "attendee");
            }
            Tracked row = Resolve(party);
            if (row == null) throw new ArgumentException("Unknown party " + ((long)party).ToString(), "party");
            if (row.Done) return;
            if (Find(row, attendee) != null) return;
            Attendance next = new Attendance();
            next.Resident = attendee;
            next.Entry = Clamp(row, world.GameSeconds);
            next.HasDraw = sentimentDraw.HasValue;
            next.Draw = sentimentDraw.HasValue ? sentimentDraw.Value : 0.0;
            row.Attendees.Add(next);
        }

        // The recorded departure boundary; repeated exits apply nothing.
        public void Exit(StateWriter writer, ActionInstanceId party, ResidentId attendee)
        {
            CheckWriter(writer);
            Tracked row = Resolve(party);
            if (row == null) throw new ArgumentException("Unknown party " + ((long)party).ToString(), "party");
            if (row.Done) return;
            Attendance att = Find(row, attendee);
            if (att == null || att.Exit >= 0L) return;
            att.Exit = Clamp(row, world.GameSeconds);
            if (att.Exit < att.Entry) att.Exit = att.Entry;
            CommitAttendance(writer, row, att);
        }

        public bool HasSaveState()
        {
            return parties.Count > 0 || plans.Count > 0;
        }

        // Live plans in creation order; LiveSimulation mutates the rows.
        public List<PartyPlan> Plans { get { return plans; } }

        // The host's open plan, if any.
        public PartyPlan OpenPlan(ResidentId host)
        {
            for (int i = 0; i < plans.Count; i++)
            {
                if (!plans[i].Closed && plans[i].Host == (long)host) return plans[i];
            }
            return null;
        }

        // Whether the factual party row closed (completed or not) and whether it completed.
        public bool IsDone(ActionInstanceId party, out bool completed)
        {
            completed = false;
            Tracked row = Resolve(party);
            if (row == null || !row.Done) return false;
            ExecutionRecord record = executor.GetExecution(row.Action);
            completed = record != null && record.IsTerminal && record.Outcome == TerminalOutcome.Completed;
            return true;
        }

        // Save capture: rows sorted by key, attendees by resident, so identical
        // state renders identical bytes.
        public PartySnapshot Export()
        {
            PartySnapshot snapshot = new PartySnapshot();
            List<Tracked> rows = new List<Tracked>(parties);
            for (int i = 1; i < rows.Count; i++)
            {
                Tracked key = rows[i];
                int j = i - 1;
                while (j >= 0 && (long)rows[j].Key > (long)key.Key) { rows[j + 1] = rows[j]; j--; }
                rows[j + 1] = key;
            }
            for (int i = 0; i < rows.Count; i++)
            {
                PartySnapshot.PartyRow saved = new PartySnapshot.PartyRow();
                saved.Key = (long)rows[i].Key;
                saved.Action = (long)rows[i].Action;
                saved.Host = (long)rows[i].Host;
                saved.Start = rows[i].Start;
                saved.Charged = rows[i].Charged;
                saved.Done = rows[i].Done;
                List<Attendance> attendees = new List<Attendance>(rows[i].Attendees);
                for (int a = 1; a < attendees.Count; a++)
                {
                    Attendance key = attendees[a];
                    int j = a - 1;
                    while (j >= 0 && (long)attendees[j].Resident > (long)key.Resident)
                    {
                        attendees[j + 1] = attendees[j];
                        j--;
                    }
                    attendees[j + 1] = key;
                }
                for (int a = 0; a < attendees.Count; a++)
                {
                    PartySnapshot.AttendeeRow line = new PartySnapshot.AttendeeRow();
                    line.Resident = (long)attendees[a].Resident;
                    line.Entry = attendees[a].Entry;
                    line.Exit = attendees[a].Exit;
                    line.HasDraw = attendees[a].HasDraw;
                    line.Draw = attendees[a].Draw;
                    saved.Attendees.Add(line);
                }
                snapshot.Rows.Add(saved);
            }
            for (int i = 0; i < plans.Count; i++) snapshot.Plans.Add(plans[i].Copy());
            return snapshot;
        }

        // Save restore: validates every row before replacing state, so a forged
        // bundle is refused instead of half-applied.
        public void Import(StateWriter writer, PartySnapshot snapshot)
        {
            CheckWriter(writer);
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            if (snapshot.Rows == null) throw new ArgumentException("Party bundle rows must exist", "snapshot");
            List<Tracked> next = new List<Tracked>();
            Dictionary<ActionInstanceId, Tracked> nextByKey = new Dictionary<ActionInstanceId, Tracked>();
            Dictionary<ActionInstanceId, Tracked> nextByAction = new Dictionary<ActionInstanceId, Tracked>();
            for (int i = 0; i < snapshot.Rows.Count; i++)
            {
                PartySnapshot.PartyRow saved = snapshot.Rows[i];
                if (saved == null || saved.Key <= 0L || saved.Action <= 0L || saved.Host <= 0L)
                {
                    throw new ArgumentException("Invalid party row " + i.ToString(), "snapshot");
                }
                Tracked row = new Tracked();
                row.Key = (ActionInstanceId)saved.Key;
                row.Action = (ActionInstanceId)saved.Action;
                row.Host = (ResidentId)saved.Host;
                row.Start = saved.Start;
                row.Charged = saved.Charged;
                row.Done = saved.Done;
                if (nextByKey.ContainsKey(row.Key) || nextByAction.ContainsKey(row.Action))
                {
                    throw new ArgumentException("Party bundle has duplicate ids", "snapshot");
                }
                ResidentRecord hostRecord;
                if (!world.TryGetResident(row.Host, out hostRecord)
                    || row.Start > world.GameSeconds)
                {
                    throw new ArgumentException("Party row " + i.ToString()
                        + " contradicts the world", "snapshot");
                }
                if (saved.Attendees == null)
                {
                    throw new ArgumentException("Party bundle attendees must exist", "snapshot");
                }
                for (int a = 0; a < saved.Attendees.Count; a++)
                {
                    PartySnapshot.AttendeeRow line = saved.Attendees[a];
                    if (line == null || line.Resident <= 0L || line.Entry < 0L
                        || line.Exit < -1L || (line.Exit >= 0L && line.Exit < line.Entry))
                    {
                        throw new ArgumentException("Invalid party attendee row " + i.ToString(), "snapshot");
                    }
                    if (Find(row, (ResidentId)line.Resident) != null)
                    {
                        throw new ArgumentException("Duplicate party attendee row " + i.ToString(), "snapshot");
                    }
                    ResidentRecord attendeeRecord;
                    if (!world.TryGetResident((ResidentId)line.Resident, out attendeeRecord)
                        || line.Entry > world.GameSeconds)
                    {
                        throw new ArgumentException("Party attendee row " + i.ToString()
                            + " contradicts the world", "snapshot");
                    }
                    Attendance att = new Attendance();
                    att.Resident = (ResidentId)line.Resident;
                    att.Entry = line.Entry;
                    att.Exit = line.Exit;
                    att.HasDraw = line.HasDraw;
                    att.Draw = line.Draw;
                    row.Attendees.Add(att);
                }
                next.Add(row);
                nextByKey.Add(row.Key, row);
                nextByAction.Add(row.Action, row);
            }
            List<PartyPlan> nextPlans = new List<PartyPlan>();
            HashSet<long> planIds = new HashSet<long>();
            for (int i = 0; i < snapshot.Plans.Count; i++)
            {
                PartyPlan saved = snapshot.Plans[i];
                ResidentRecord hostRecord;
                if (saved == null || saved.Id <= 0L || !planIds.Add(saved.Id) || saved.HostAction < 0L
                    || !world.TryGetResident((ResidentId)saved.Host, out hostRecord))
                {
                    throw new ArgumentException("Invalid party plan " + i.ToString(), "snapshot");
                }
                for (int g = 0; g < saved.Invites.Count; g++)
                {
                    ResidentRecord guest;
                    if (saved.Invites[g] == null || saved.Invites[g].Guest == saved.Host
                        || !world.TryGetResident((ResidentId)saved.Invites[g].Guest, out guest))
                    {
                        throw new ArgumentException("Invalid party invite " + i.ToString(), "snapshot");
                    }
                }
                nextPlans.Add(saved.Copy());
            }
            plans.Clear();
            plans.AddRange(nextPlans);
            parties.Clear();
            byKey.Clear();
            byAction.Clear();
            for (int i = 0; i < next.Count; i++)
            {
                parties.Add(next[i]);
                byKey.Add(next[i].Key, next[i]);
                byAction.Add(next[i].Action, next[i]);
            }
        }

        private void Sweep()
        {
            ResidentRecord[] residents = world.ResidentsSnapshot();
            for (int i = 0; i < residents.Length; i++)
            {
                ResidentId id = residents[i].Id;
                if (!residents[i].IsAlive) continue;
                Adopt(id, arbitrator.GetCurrent(id));
            }
        }

        private void Adopt(ResidentId resident, ActionEntry entry)
        {
            if (entry == null || entry.Kind != CommandKind.DirectActivity) return;
            if (!string.Equals(entry.Activity.Code, ActivityCode, StringComparison.Ordinal)) return;
            if (byAction.ContainsKey(entry.Action) || byKey.ContainsKey(entry.Action)) return;
            Tracked row = new Tracked();
            row.Key = entry.Action;
            row.Action = entry.Action;
            row.Host = resident;
            parties.Add(row);
            byKey.Add(row.Key, row);
            byAction.Add(row.Action, row);
        }

        private void Observe(StateWriter writer, Tracked row)
        {
            if (row.Done) return;
            ExecutionRecord record = executor.GetExecution(row.Action);
            if (record == null) return;
            if (record.Resident != row.Host
                || !string.Equals(record.Activity.Code, ActivityCode, StringComparison.Ordinal))
            {
                // A loaded action id must still name this host's own party; a
                // forged binding is closed without paying anything.
                row.Done = true;
                return;
            }
            long start;
            if (row.Start < 0L && TryPerformStart(record, out start))
            {
                row.Start = start;
                Charge(writer, row);
            }
            if (!record.IsTerminal) return;
            row.Done = true;
            if (record.Outcome == TerminalOutcome.Completed)
            {
                Finish(writer, row);
                return;
            }
            // Interrupted, failed and abandoned parties pay no completion terms,
            // but the event end is every open attendee's exit: commit what they attended.
            CloseOpenAttendance(writer, row);
        }

        private void CloseOpenAttendance(StateWriter writer, Tracked row)
        {
            long closed = Clamp(row, world.GameSeconds);
            for (int i = 0; i < row.Attendees.Count; i++)
            {
                Attendance att = row.Attendees[i];
                if (att.Exit >= 0L) continue;
                att.Exit = closed < att.Entry ? att.Entry : closed;
                CommitAttendance(writer, row, att);
            }
        }

        private void Charge(StateWriter writer, Tracked row)
        {
            if (row.Charged) return;
            row.Charged = true;
            accounts.Transfer(writer, row.Action, AccountRef.Personal(row.Host),
                AccountRef.Expense(ExpenseAccount), HostCost);
        }

        private void Finish(StateWriter writer, Tracked row)
        {
            long now = world.GameSeconds;
            CloseOpenAttendance(writer, row);
            WorldEvent factual = world.AppendEvent(writer, EventKind, row.Host, ActivityCode);
            bool[] chatted = new bool[row.Attendees.Count];
            RecordChats(writer, row, chatted);
            List<ResidentId> participants = new List<ResidentId>();
            for (int i = 0; i < row.Attendees.Count; i++) participants.Add(row.Attendees[i].Resident);
            SortResidents(participants);
            int stayed = 0;
            for (int i = 0; i < row.Attendees.Count; i++)
            {
                Attendance att = row.Attendees[i];
                long attended = att.Exit - att.Entry;
                if (attended < EligibleGameSeconds) continue;
                stayed++;
                if (!chatted[i]) continue;
                if (!att.HasDraw)
                {
                    att.Draw = world.NextRelationshipRandom(writer);
                    att.HasDraw = true;
                }
                ResidentRecord resident;
                if (!world.TryGetResident(att.Resident, out resident)) continue;
                float friendship;
                float romance;
                BondToward(resident, row.Host, out friendship, out romance);
                double probability = Autonomy.SentimentProbability(
                    resident.GetTrait(TraitAxis.Sociable), resident.GetNeed(Need.Social), friendship);
                if (att.Draw < probability)
                {
                    world.AddFeeling(writer, att.Resident, FeelingKind.Grateful, factual.Event,
                        row.Host, SentimentIntensity, now + GameDaySeconds);
                }
            }
            if (stayed >= HostProudAttendees)
            {
                world.AddFeeling(writer, row.Host, FeelingKind.Proud, factual.Event,
                    row.Host, SentimentIntensity, now + GameDaySeconds);
            }
            for (int i = 0; i < row.Attendees.Count; i++)
            {
                ResidentRecord resident;
                if (!world.TryGetResident(row.Attendees[i].Resident, out resident)) continue;
                world.AddMemory(writer, row.Attendees[i].Resident, new MemoryEntry(factual.Event,
                    factual.Kind, factual.GameSeconds, resident.Home, participants,
                    AttendedOutcome, ParticipantLearnedBy));
            }
        }

        // Pair gains and chat eligibility read the factual completed-Chat
        // events inside both recorded intervals, so a reload adds no new facts.
        private void RecordChats(StateWriter writer, Tracked row, bool[] chatted)
        {
            IReadOnlyList<WorldEvent> events = world.Events;
            for (int i = 0; i < events.Count; i++)
            {
                WorldEvent entry = events[i];
                if (!string.Equals(entry.Kind, ChatKind, StringComparison.Ordinal)) continue;
                if (!entry.Actor.HasValue || entry.Detail == null
                    || !entry.Detail.StartsWith(ChatDetailPrefix, StringComparison.Ordinal)) continue;
                long listener;
                if (!long.TryParse(entry.Detail.Substring(ChatDetailPrefix.Length), out listener)) continue;
                int first = IndexOf(row, entry.Actor.Value);
                int second = IndexOf(row, (ResidentId)listener);
                if (first < 0 || second < 0 || first == second) continue;
                if (!Within(row.Attendees[first], entry.GameSeconds)
                    || !Within(row.Attendees[second], entry.GameSeconds)) continue;
                chatted[first] = true;
                chatted[second] = true;
                ApplyPairBonus(writer, row, row.Attendees[first].Resident,
                    row.Attendees[second].Resident);
            }
        }

        private void CommitAttendance(StateWriter writer, Tracked row, Attendance att)
        {
            long attended = att.Exit - att.Entry;
            if (attended < 0L) attended = 0L;
            if (attended > DurationGameSeconds) attended = DurationGameSeconds;
            ResidentRecord resident;
            if (!world.TryGetResident(att.Resident, out resident)) return;
            LedgerRecipient recipient = new LedgerRecipient(att.Resident);
            if (!world.ReserveEffectKeyed(writer, row.Action, AttendanceEffectKind, recipient)) return;
            float fraction = (float)attended / (float)DurationGameSeconds;
            world.SetNeed(writer, att.Resident, Need.Social,
                resident.GetNeed(Need.Social) + SocialGain * fraction);
            world.SetNeed(writer, att.Resident, Need.Fun,
                resident.GetNeed(Need.Fun) + FunGain * fraction);
            world.SetNeed(writer, att.Resident, Need.Energy,
                resident.GetNeed(Need.Energy) + EnergyGain * fraction);
            world.CommitEffectKeyed(writer, row.Action, AttendanceEffectKind, recipient);
        }

        private void ApplyPairBonus(StateWriter writer, Tracked row, ResidentId first, ResidentId second)
        {
            ResidentRecord a;
            ResidentRecord b;
            if (!world.TryGetResident(first, out a) || !world.TryGetResident(second, out b)) return;
            // Unordered pair: the ledger key must not depend on who spoke first.
            if ((long)first > (long)second)
            {
                ResidentId swap = first;
                first = second;
                second = swap;
                ResidentRecord swapRecord = a;
                a = b;
                b = swapRecord;
            }
            LedgerRecipient pair = new LedgerRecipient(first, second);
            if (!world.ReserveEffectKeyed(writer, row.Action, PairEffectKind, pair)) return;
            float friendship;
            float romance;
            BondToward(a, second, out friendship, out romance);
            world.SetBond(writer, first, second, friendship + PairFriendshipGain, romance);
            BondToward(b, first, out friendship, out romance);
            world.SetBond(writer, second, first, friendship + PairFriendshipGain, romance);
            world.CommitEffectKeyed(writer, row.Action, PairEffectKind, pair);
        }

        private Tracked Resolve(ActionInstanceId party)
        {
            Tracked row;
            if (byKey.TryGetValue(party, out row)) return row;
            if (byAction.TryGetValue(party, out row)) return row;
            // A caller may arrive between Submit and the next Tick; adopt the
            // matching current action now so the id resolves on first use.
            ResidentRecord[] residents = world.ResidentsSnapshot();
            for (int i = 0; i < residents.Length; i++)
            {
                if (!residents[i].IsAlive) continue;
                ActionEntry entry = arbitrator.GetCurrent(residents[i].Id);
                if (entry == null || entry.Action != party) continue;
                Adopt(residents[i].Id, entry);
                if (byKey.TryGetValue(party, out row)) return row;
                if (byAction.TryGetValue(party, out row)) return row;
            }
            return null;
        }

        private static long Clamp(Tracked row, long gameSeconds)
        {
            if (row.Start < 0L) return gameSeconds;
            long end = row.Start + DurationGameSeconds;
            if (gameSeconds < row.Start) return row.Start;
            return gameSeconds > end ? end : gameSeconds;
        }

        private static bool TryPerformStart(ExecutionRecord record, out long gameSeconds)
        {
            for (int i = 0; i < record.Trace.Count; i++)
            {
                if (record.Trace[i].Stage == ExecutionStage.Perform && !record.Trace[i].Skipped)
                {
                    gameSeconds = record.Trace[i].GameSeconds;
                    return true;
                }
            }
            gameSeconds = -1L;
            return false;
        }

        private static Attendance Find(Tracked row, ResidentId resident)
        {
            for (int i = 0; i < row.Attendees.Count; i++)
            {
                if (row.Attendees[i].Resident == resident) return row.Attendees[i];
            }
            return null;
        }

        private static int IndexOf(Tracked row, ResidentId resident)
        {
            for (int i = 0; i < row.Attendees.Count; i++)
            {
                if (row.Attendees[i].Resident == resident) return i;
            }
            return -1;
        }

        private static bool Within(Attendance att, long gameSeconds)
        {
            return gameSeconds >= att.Entry && att.Exit >= 0L && gameSeconds <= att.Exit;
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
            if (writer != StateWriter.SimCore && writer != StateWriter.CommandArbitrator
                && writer != StateWriter.ActivityExecutor)
            {
                throw new ArgumentException("Unknown state writer", "writer");
            }
        }
    }
}
