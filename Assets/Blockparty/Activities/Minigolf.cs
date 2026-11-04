// MINIGOLF@1: Courtyard Minigolf (BP-ACT-08, ACT-SPORT-02).
// Consumes STATE@1 (WorldState, StateWriter, EffectLedger), COMMAND@2
// (CommandArbitrator, CommandEnvelope), NAV@2 (ActivityExecutor slots) and
// CLOCK@1 (shared game seconds). Plain C# 9, no engine dependency.
// One shared match ActionInstanceId owns the whole course reservation and all
// terminal effects; each participant has a dependent activity action that is
// released on completion or whole-match cancellation. Participant order is
// ascending ResidentId and control modes are fixed at entry. Physics advances
// from the shared game clock: 60 game seconds are one normal-play second, so
// friction is metres per normal-play second squared. A putt is forced to rest
// at 480 game seconds (8 normal-play seconds). Every authoritative mutation
// flows through an audited WorldState method with StateWriter.ActivityExecutor.
// Fast Enter Play Mode: no static mutable state, explicit numeric conversions,
// no AppDomain, Assembly.Location, BinaryFormatter or wall-clock reads.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;
using Blockparty.Simulation;

namespace Blockparty.Activities
{
    public sealed class Minigolf
    {
        public const string ActivityCode = "ACT-SPORT-02";
        public const int HoleCount = 3;
        public const int MaxStrokes = 6;
        public const int MaxParticipants = 4;
        public const long AimGameSeconds = 60L;
        public const long ForcedRestGameSeconds = 480L;

        private const StateWriter Writer = StateWriter.ActivityExecutor;
        private const double GameSecondsPerNormalSecond = 60.0;
        private const double SubstepGameSeconds = 0.5;
        private const string FunKind = "Fun";
        private const string SocialKind = "Social";
        private const string FitnessKind = "Fitness";
        private const string ProudKind = "Proud";
        private const string FriendshipKind = "Friendship";

        public readonly struct Hole
        {
            public readonly ApproachPose Tee;
            public readonly ApproachPose Cup;
            public readonly double MinX;
            public readonly double MaxX;
            public readonly double MinZ;
            public readonly double MaxZ;
            public readonly double CupRadius;

            public Hole(ApproachPose tee, ApproachPose cup, double minX, double maxX,
                double minZ, double maxZ, double cupRadius)
            {
                Tee = tee;
                Cup = cup;
                MinX = minX;
                MaxX = maxX;
                MinZ = minZ;
                MaxZ = maxZ;
                CupRadius = cupRadius;
            }
        }

        // Detached diagnostic copies: mutation never reaches live match state.
        public sealed class ParticipantSnapshot
        {
            public ResidentId Resident;
            public bool Human;
            public bool Winner;
            public int[] Strokes = new int[HoleCount];
            public bool[] HoleComplete = new bool[HoleCount];
            public ApproachPose BallPosition;
            public ApproachPose BallVelocity;
        }

        public sealed class PuttSnapshot
        {
            public ResidentId Resident;
            public int HoleIndex;
            public int TurnIndex;
            public int Degrees;
            public int Power;
            public double? RandomDraw;
        }

        public sealed class MatchSnapshot
        {
            public ActionInstanceId Action;
            public int HoleIndex;
            public int TurnIndex;
            public ResidentId ActiveResident;
            public bool Rolling;
            public bool Completed;
            public bool Cancelled;
            public long ElapsedGameSeconds;
            public long PuttElapsedGameSeconds;
            public long AimElapsedGameSeconds;
            public int AimDegrees;
            public int AimPower;
            public int PendingConfirmationCount;
            public ulong RandomState;
            public ParticipantSnapshot[] Participants = new ParticipantSnapshot[0];
            public PuttSnapshot[] Putts = new PuttSnapshot[0];
        }

        // Save bundle: course content plus every match checkpoint. Action ids
        // are the saved ids; SAVE remaps dependent participant actions through
        // the saved-to-fresh action map before Import, and never remaps the
        // shared match id.
        public sealed class MinigolfSnapshot
        {
            public sealed class HoleRow
            {
                public long Course;
                public float TeeX;
                public float TeeY;
                public float TeeZ;
                public float CupX;
                public float CupY;
                public float CupZ;
                public double MinX;
                public double MaxX;
                public double MinZ;
                public double MaxZ;
                public double CupRadius;
                public int Index;
            }

            public sealed class CourseRow
            {
                public long Course;
                public double Friction;
                public double MetresPerSecondPerPower;
            }

            public sealed class ParticipantRow
            {
                public long Resident;
                public bool Human;
                public bool Winner;
                public int Stroke0;
                public int Stroke1;
                public int Stroke2;
                public bool Complete0;
                public bool Complete1;
                public bool Complete2;
                public float X;
                public float Y;
                public float Z;
                public float VelocityX;
                public float VelocityY;
                public float VelocityZ;
            }

            public sealed class PuttRow
            {
                public long Resident;
                public int HoleIndex;
                public int TurnIndex;
                public int Degrees;
                public int Power;
                public bool HasDraw;
                public double Draw;
            }

            public sealed class MatchRow
            {
                public long Action;
                public long Course;
                public int HoleIndex;
                public int TurnIndex;
                public int ActiveIndex;
                public bool Rolling;
                public bool Completed;
                public bool Cancelled;
                public bool CancelRequested;
                public bool HasPending;
                public int PendingDegrees;
                public int PendingPower;
                public int AimDegrees;
                public int AimPower;
                public long StartedAt;
                public long LastTick;
                public long TurnStart;
                public long PuttStart;
                public float PuttRestX;
                public float PuttRestY;
                public float PuttRestZ;
                public ulong RandomState;
                public bool EffectsApplied;
                public readonly List<ParticipantRow> Participants = new List<ParticipantRow>();
                public readonly List<PuttRow> Putts = new List<PuttRow>();
            }

            public readonly List<CourseRow> Courses = new List<CourseRow>();
            public readonly List<HoleRow> Holes = new List<HoleRow>();
            public readonly List<MatchRow> Matches = new List<MatchRow>();
        }

        private sealed class Course
        {
            public ObjectId Id;
            public Hole[] Holes;
            public double Friction;
            public double MetresPerSecondPerPower;
        }

        private sealed class Participant
        {
            public ResidentId Resident;
            public bool Human;
            public bool Winner;
            public readonly int[] Strokes = new int[HoleCount];
            public readonly bool[] Complete = new bool[HoleCount];
            public float X;
            public float Y;
            public float Z;
            public float VelocityX;
            public float VelocityY;
            public float VelocityZ;
        }

        private sealed class PuttRecord
        {
            public ResidentId Resident;
            public int Hole;
            public int Turn;
            public int Degrees;
            public int Power;
            public double? Draw;
        }

        private sealed class Match
        {
            public ActionInstanceId Action;
            public ObjectId CourseId;
            public Course Course;
            public readonly List<Participant> Participants = new List<Participant>();
            public readonly List<PuttRecord> Putts = new List<PuttRecord>();
            public int HoleIndex;
            public int TurnIndex;
            public int ActiveIndex;
            public bool Rolling;
            public bool Completed;
            public bool Cancelled;
            public bool CancelRequested;
            public bool HasPending;
            public int PendingDegrees;
            public int PendingPower;
            public int AimDegrees;
            public int AimPower;
            public long StartedAt;
            public long LastTick;
            public long TurnStart;
            public long PuttStart;
            public float PuttRestX;
            public float PuttRestY;
            public float PuttRestZ;
            public bool EffectsApplied;
            public WorldRng Rng;
        }

        private readonly WorldState world;
        private readonly CommandArbitrator arbitrator;
        private readonly ActivityExecutor executor;
        private readonly Clock clock;
        private readonly Dictionary<ObjectId, Course> courses = new Dictionary<ObjectId, Course>();
        private readonly Dictionary<ActionInstanceId, Match> matches = new Dictionary<ActionInstanceId, Match>();
        private bool unsafeWeather;

        public Minigolf(WorldState world, CommandArbitrator arbitrator,
            ActivityExecutor executor, Clock clock)
        {
            if (world == null) throw new ArgumentNullException("world");
            if (arbitrator == null) throw new ArgumentNullException("arbitrator");
            if (executor == null) throw new ArgumentNullException("executor");
            if (clock == null) throw new ArgumentNullException("clock");
            this.world = world;
            this.arbitrator = arbitrator;
            this.executor = executor;
            this.clock = clock;
            executor.MinigolfService = this;
        }

        public static Minigolf FromExecutor(ActivityExecutor executor)
        {
            if (executor == null)
            {
                throw new ArgumentNullException("executor");
            }
            return executor.MinigolfService;
        }

        public void RegisterCourse(ObjectId course, Hole[] holes, double friction,
            double metresPerSecondPerPower)
        {
            if (holes == null || holes.Length != HoleCount)
            {
                throw new ArgumentException("A minigolf course needs exactly 3 holes", "holes");
            }
            if (!IsFinite(friction) || friction < 0.0)
            {
                throw new ArgumentOutOfRangeException("friction", "Friction is finite and non-negative");
            }
            if (!IsFinite(metresPerSecondPerPower) || metresPerSecondPerPower <= 0.0)
            {
                throw new ArgumentOutOfRangeException("metresPerSecondPerPower",
                    "Launch speed per power must be positive");
            }
            for (int i = 0; i < holes.Length; i++)
            {
                if (!IsFinite(holes[i].MinX) || !IsFinite(holes[i].MaxX)
                    || !IsFinite(holes[i].MinZ) || !IsFinite(holes[i].MaxZ)
                    || holes[i].MinX >= holes[i].MaxX || holes[i].MinZ >= holes[i].MaxZ
                    || !IsFinite(holes[i].CupRadius) || holes[i].CupRadius <= 0.0)
                {
                    throw new ArgumentException("Hole " + i.ToString() + " has invalid bounds", "holes");
                }
            }
            Course entry = new Course();
            entry.Id = course;
            entry.Holes = CopyHoles(holes);
            entry.Friction = friction;
            entry.MetresPerSecondPerPower = metresPerSecondPerPower;
            courses[course] = entry;
        }

        public ActionInstanceId Start(ObjectId course, ResidentId[] participants, bool[] human)
        {
            Course registered;
            if (!courses.TryGetValue(course, out registered))
            {
                throw new ArgumentException("Course " + ((long)course).ToString()
                    + " is not registered", "course");
            }
            if (participants == null) throw new ArgumentNullException("participants");
            if (human == null) throw new ArgumentNullException("human");
            if (participants.Length != human.Length)
            {
                throw new ArgumentException("Control modes must match participants", "human");
            }
            if (participants.Length < 1 || participants.Length > MaxParticipants)
            {
                throw new ArgumentException("A match needs 1 to 4 participants", "participants");
            }
            foreach (Match active in matches.Values)
            {
                if (!active.Completed && !active.Cancelled && active.CourseId == course)
                {
                    throw new InvalidOperationException("The course already holds a live match");
                }
            }
            List<Participant> ordered = new List<Participant>();
            for (int i = 0; i < participants.Length; i++)
            {
                ResidentRecord record;
                if (!world.TryGetResident(participants[i], out record))
                {
                    throw new ArgumentException("Unknown participant "
                        + ((long)participants[i]).ToString(), "participants");
                }
                if (!record.IsAlive)
                {
                    throw new ArgumentException("Deceased participant "
                        + ((long)participants[i]).ToString(), "participants");
                }
                for (int j = 0; j < ordered.Count; j++)
                {
                    if (ordered[j].Resident == participants[i])
                    {
                        throw new ArgumentException("Duplicate participant "
                            + ((long)participants[i]).ToString(), "participants");
                    }
                }
                Participant entry = new Participant();
                entry.Resident = participants[i];
                entry.Human = human[i];
                ordered.Add(entry);
            }
            SortParticipants(ordered);

            ActionInstanceId action = world.AllocateAction(StateWriter.SimCore);
            Match match = new Match();
            match.Action = action;
            match.CourseId = course;
            match.Course = registered;
            match.StartedAt = world.GameSeconds;
            match.LastTick = world.GameSeconds;
            match.TurnStart = world.GameSeconds;
            match.HoleIndex = 0;
            match.TurnIndex = 0;
            match.ActiveIndex = 0;
            match.Rng = new WorldRng(SeedFor(world.Seed, action));
            for (int i = 0; i < ordered.Count; i++)
            {
                Participant participant = ordered[i];
                participant.X = registered.Holes[0].Tee.X;
                participant.Y = registered.Holes[0].Tee.Y;
                participant.Z = registered.Holes[0].Tee.Z;
                match.Participants.Add(participant);
            }
            matches[action] = match;

            // Fixed mode/order entry takes over any previous participant
            // action; the match itself is one shared executor reservation, so
            // no participant queue entry is created for it.
            for (int i = 0; i < match.Participants.Count; i++)
            {
                TakeOver(match.Participants[i].Resident);
            }
            ActionEntry dispatchEntry = new ActionEntry(world.AllocateCommand(StateWriter.SimCore),
                action, CommandKind.DirectActivity, new ActivityId(ActivityCode),
                (long)course, ActionOwner.Player, CommandSource.PlayerHuman);
            executor.Dispatch(match.Participants[0].Resident, dispatchEntry);
            return action;
        }

        public MatchSnapshot GetMatch(ActionInstanceId action)
        {
            Match match;
            if (!matches.TryGetValue(action, out match))
            {
                return null;
            }
            return Detach(match);
        }

        public bool SubmitPutt(ActionInstanceId action, int holeIndex, int turnIndex,
            ResidentId resident, int degrees, int power)
        {
            Match match;
            if (!matches.TryGetValue(action, out match)) return false;
            if (match.Completed || match.Cancelled || match.Rolling) return false;
            if (holeIndex != match.HoleIndex || turnIndex != match.TurnIndex) return false;
            if (resident != match.Participants[match.ActiveIndex].Resident) return false;
            Participant participant = match.Participants[match.ActiveIndex];
            if (!participant.Human) return false;
            if (degrees < 0 || degrees > 355 || degrees % 5 != 0) return false;
            if (power < 0 || power > 100 || power % 5 != 0) return false;
            if (clock.Paused)
            {
                match.HasPending = true;
                match.PendingDegrees = degrees;
                match.PendingPower = power;
                match.AimDegrees = degrees;
                match.AimPower = power;
                return true;
            }
            Launch(match, participant, degrees, power, null);
            return true;
        }

        public bool TrySetHuman(ActionInstanceId action, ResidentId resident, bool human)
        {
            Match match;
            if (!matches.TryGetValue(action, out match)) return false;
            if (match.Completed || match.Cancelled) return false;
            if (FindParticipant(match, resident) < 0) return false;
            // Control mode is fixed at match entry; only cancellation changes it.
            return false;
        }

        public void Leave(ActionInstanceId action, ResidentId resident)
        {
            Match match;
            if (!matches.TryGetValue(action, out match)) return;
            if (match.Completed || match.Cancelled) return;
            if (FindParticipant(match, resident) < 0) return;
            match.CancelRequested = true;
        }

        public void SetUnsafeWeather(bool unsafeWeather)
        {
            this.unsafeWeather = unsafeWeather;
        }

        public void Tick(StateWriter writer)
        {
            CheckWriter(writer);
            executor.Tick(StateWriter.ActivityExecutor);
            if (clock.Paused)
            {
                return;
            }
            List<Match> live = new List<Match>();
            foreach (KeyValuePair<ActionInstanceId, Match> pair in matches)
            {
                if (!pair.Value.Completed && !pair.Value.Cancelled)
                {
                    live.Add(pair.Value);
                }
            }
            for (int i = 0; i < live.Count; i++)
            {
                StepMatch(live[i]);
            }
        }

        public MinigolfSnapshot Export()
        {
            MinigolfSnapshot snapshot = new MinigolfSnapshot();
            List<ObjectId> courseIds = new List<ObjectId>(courses.Keys);
            SortObjects(courseIds);
            for (int i = 0; i < courseIds.Count; i++)
            {
                Course course = courses[courseIds[i]];
                MinigolfSnapshot.CourseRow row = new MinigolfSnapshot.CourseRow();
                row.Course = (long)course.Id;
                row.Friction = course.Friction;
                row.MetresPerSecondPerPower = course.MetresPerSecondPerPower;
                snapshot.Courses.Add(row);
                for (int h = 0; h < course.Holes.Length; h++)
                {
                    MinigolfSnapshot.HoleRow hole = new MinigolfSnapshot.HoleRow();
                    hole.Course = (long)course.Id;
                    hole.Index = h;
                    hole.TeeX = course.Holes[h].Tee.X;
                    hole.TeeY = course.Holes[h].Tee.Y;
                    hole.TeeZ = course.Holes[h].Tee.Z;
                    hole.CupX = course.Holes[h].Cup.X;
                    hole.CupY = course.Holes[h].Cup.Y;
                    hole.CupZ = course.Holes[h].Cup.Z;
                    hole.MinX = course.Holes[h].MinX;
                    hole.MaxX = course.Holes[h].MaxX;
                    hole.MinZ = course.Holes[h].MinZ;
                    hole.MaxZ = course.Holes[h].MaxZ;
                    hole.CupRadius = course.Holes[h].CupRadius;
                    snapshot.Holes.Add(hole);
                }
            }
            List<ActionInstanceId> actionIds = new List<ActionInstanceId>(matches.Keys);
            SortActions(actionIds);
            for (int i = 0; i < actionIds.Count; i++)
            {
                Match match = matches[actionIds[i]];
                MinigolfSnapshot.MatchRow row = new MinigolfSnapshot.MatchRow();
                row.Action = (long)match.Action;
                row.Course = (long)match.CourseId;
                row.HoleIndex = match.HoleIndex;
                row.TurnIndex = match.TurnIndex;
                row.ActiveIndex = match.ActiveIndex;
                row.Rolling = match.Rolling;
                row.Completed = match.Completed;
                row.Cancelled = match.Cancelled;
                row.CancelRequested = match.CancelRequested;
                row.HasPending = match.HasPending;
                row.PendingDegrees = match.PendingDegrees;
                row.PendingPower = match.PendingPower;
                row.AimDegrees = match.AimDegrees;
                row.AimPower = match.AimPower;
                row.StartedAt = match.StartedAt;
                row.LastTick = match.LastTick;
                row.TurnStart = match.TurnStart;
                row.PuttStart = match.PuttStart;
                row.PuttRestX = match.PuttRestX;
                row.PuttRestY = match.PuttRestY;
                row.PuttRestZ = match.PuttRestZ;
                row.RandomState = match.Rng.State;
                row.EffectsApplied = match.EffectsApplied;
                for (int p = 0; p < match.Participants.Count; p++)
                {
                    Participant participant = match.Participants[p];
                    MinigolfSnapshot.ParticipantRow participantRow = new MinigolfSnapshot.ParticipantRow();
                    participantRow.Resident = (long)participant.Resident;
                    participantRow.Human = participant.Human;
                    participantRow.Winner = participant.Winner;
                    participantRow.Stroke0 = participant.Strokes[0];
                    participantRow.Stroke1 = participant.Strokes[1];
                    participantRow.Stroke2 = participant.Strokes[2];
                    participantRow.Complete0 = participant.Complete[0];
                    participantRow.Complete1 = participant.Complete[1];
                    participantRow.Complete2 = participant.Complete[2];
                    participantRow.X = participant.X;
                    participantRow.Y = participant.Y;
                    participantRow.Z = participant.Z;
                    participantRow.VelocityX = participant.VelocityX;
                    participantRow.VelocityY = participant.VelocityY;
                    participantRow.VelocityZ = participant.VelocityZ;
                    row.Participants.Add(participantRow);
                }
                for (int p = 0; p < match.Putts.Count; p++)
                {
                    PuttRecord putt = match.Putts[p];
                    MinigolfSnapshot.PuttRow puttRow = new MinigolfSnapshot.PuttRow();
                    puttRow.Resident = (long)putt.Resident;
                    puttRow.HoleIndex = putt.Hole;
                    puttRow.TurnIndex = putt.Turn;
                    puttRow.Degrees = putt.Degrees;
                    puttRow.Power = putt.Power;
                    puttRow.HasDraw = putt.Draw.HasValue;
                    puttRow.Draw = putt.Draw.HasValue ? putt.Draw.Value : 0.0;
                    row.Putts.Add(puttRow);
                }
                snapshot.Matches.Add(row);
            }
            return snapshot;
        }

        public void Import(StateWriter writer, MinigolfSnapshot snapshot)
        {
            CheckWriter(writer);
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            if (snapshot.Courses == null || snapshot.Holes == null || snapshot.Matches == null)
            {
                throw new ArgumentException("Minigolf bundle lists must exist", "snapshot");
            }
            Dictionary<ObjectId, Course> nextCourses = new Dictionary<ObjectId, Course>();
            for (int i = 0; i < snapshot.Courses.Count; i++)
            {
                MinigolfSnapshot.CourseRow row = snapshot.Courses[i];
                if (row == null) throw new ArgumentException("Null minigolf course row", "snapshot");
                ObjectId id = (ObjectId)row.Course;
                if (nextCourses.ContainsKey(id))
                {
                    throw new ArgumentException("Duplicate minigolf course " + row.Course.ToString(), "snapshot");
                }
                if (!IsFinite(row.Friction) || row.Friction < 0.0
                    || !IsFinite(row.MetresPerSecondPerPower) || row.MetresPerSecondPerPower <= 0.0)
                {
                    throw new ArgumentException("Invalid minigolf course physics", "snapshot");
                }
                Course course = new Course();
                course.Id = id;
                course.Friction = row.Friction;
                course.MetresPerSecondPerPower = row.MetresPerSecondPerPower;
                course.Holes = new Hole[HoleCount];
                nextCourses[id] = course;
            }
            for (int i = 0; i < snapshot.Holes.Count; i++)
            {
                MinigolfSnapshot.HoleRow row = snapshot.Holes[i];
                if (row == null) throw new ArgumentException("Null minigolf hole row", "snapshot");
                Course course;
                if (!nextCourses.TryGetValue((ObjectId)row.Course, out course))
                {
                    throw new ArgumentException("Minigolf hole names an unknown course", "snapshot");
                }
                if (row.Index < 0 || row.Index >= HoleCount)
                {
                    throw new ArgumentException("Minigolf hole index is out of range", "snapshot");
                }
                if (course.Holes[row.Index].CupRadius > 0.0)
                {
                    throw new ArgumentException("Duplicate minigolf hole", "snapshot");
                }
                if (!IsFinite(row.TeeX) || !IsFinite(row.TeeY) || !IsFinite(row.TeeZ)
                    || !IsFinite(row.CupX) || !IsFinite(row.CupY) || !IsFinite(row.CupZ)
                    || !IsFinite(row.MinX) || !IsFinite(row.MaxX)
                    || !IsFinite(row.MinZ) || !IsFinite(row.MaxZ)
                    || row.MinX >= row.MaxX || row.MinZ >= row.MaxZ
                    || !IsFinite(row.CupRadius) || row.CupRadius <= 0.0)
                {
                    throw new ArgumentException("Invalid minigolf hole geometry", "snapshot");
                }
                course.Holes[row.Index] = new Hole(
                    new ApproachPose(row.TeeX, row.TeeY, row.TeeZ),
                    new ApproachPose(row.CupX, row.CupY, row.CupZ),
                    row.MinX, row.MaxX, row.MinZ, row.MaxZ, row.CupRadius);
            }
            foreach (KeyValuePair<ObjectId, Course> pair in nextCourses)
            {
                for (int h = 0; h < HoleCount; h++)
                {
                    if (pair.Value.Holes[h].CupRadius <= 0.0)
                    {
                        throw new ArgumentException("Minigolf course is missing a hole", "snapshot");
                    }
                }
            }
            Dictionary<ActionInstanceId, Match> nextMatches = new Dictionary<ActionInstanceId, Match>();
            for (int i = 0; i < snapshot.Matches.Count; i++)
            {
                MinigolfSnapshot.MatchRow row = snapshot.Matches[i];
                if (row == null || row.Action <= 0L)
                {
                    throw new ArgumentException("Invalid minigolf match row", "snapshot");
                }
                ActionInstanceId action = (ActionInstanceId)row.Action;
                if (nextMatches.ContainsKey(action))
                {
                    throw new ArgumentException("Duplicate minigolf match " + row.Action.ToString(), "snapshot");
                }
                Course course;
                if (!nextCourses.TryGetValue((ObjectId)row.Course, out course))
                {
                    throw new ArgumentException("Minigolf match names an unknown course", "snapshot");
                }
                if (row.HoleIndex < 0 || row.HoleIndex >= HoleCount)
                {
                    throw new ArgumentException("Minigolf match hole is out of range", "snapshot");
                }
                if (row.ActiveIndex < 0 || row.ActiveIndex >= row.Participants.Count)
                {
                    throw new ArgumentException("Minigolf match active participant is out of range", "snapshot");
                }
                Match match = new Match();
                match.Action = action;
                match.CourseId = (ObjectId)row.Course;
                match.Course = course;
                match.HoleIndex = row.HoleIndex;
                match.TurnIndex = row.TurnIndex < 0 ? 0 : row.TurnIndex;
                match.ActiveIndex = row.ActiveIndex;
                match.Rolling = row.Rolling;
                match.Completed = row.Completed;
                match.Cancelled = row.Cancelled;
                match.CancelRequested = row.CancelRequested;
                match.HasPending = row.HasPending;
                match.PendingDegrees = row.PendingDegrees;
                match.PendingPower = row.PendingPower;
                match.AimDegrees = row.AimDegrees;
                match.AimPower = row.AimPower;
                match.StartedAt = row.StartedAt;
                match.LastTick = row.LastTick;
                match.TurnStart = row.TurnStart;
                match.PuttStart = row.PuttStart;
                match.PuttRestX = row.PuttRestX;
                match.PuttRestY = row.PuttRestY;
                match.PuttRestZ = row.PuttRestZ;
                match.Rng = WorldRng.FromState(row.RandomState);
                match.EffectsApplied = row.EffectsApplied;
                if (match.Completed && match.Cancelled)
                {
                    throw new ArgumentException("Minigolf match is both completed and cancelled", "snapshot");
                }
                for (int p = 0; p < row.Participants.Count; p++)
                {
                    MinigolfSnapshot.ParticipantRow saved = row.Participants[p];
                    if (saved == null) throw new ArgumentException("Null minigolf participant row", "snapshot");
                    if (saved.Stroke0 < 0 || saved.Stroke0 > MaxStrokes
                        || saved.Stroke1 < 0 || saved.Stroke1 > MaxStrokes
                        || saved.Stroke2 < 0 || saved.Stroke2 > MaxStrokes)
                    {
                        throw new ArgumentException("Minigolf stroke count is out of range", "snapshot");
                    }
                    Participant participant = new Participant();
                    participant.Resident = (ResidentId)saved.Resident;
                    participant.Human = saved.Human;
                    participant.Winner = saved.Winner;
                    participant.Strokes[0] = saved.Stroke0;
                    participant.Strokes[1] = saved.Stroke1;
                    participant.Strokes[2] = saved.Stroke2;
                    participant.Complete[0] = saved.Complete0;
                    participant.Complete[1] = saved.Complete1;
                    participant.Complete[2] = saved.Complete2;
                    participant.X = saved.X;
                    participant.Y = saved.Y;
                    participant.Z = saved.Z;
                    participant.VelocityX = saved.VelocityX;
                    participant.VelocityY = saved.VelocityY;
                    participant.VelocityZ = saved.VelocityZ;
                    match.Participants.Add(participant);
                }
                if (match.Participants.Count < 1 || match.Participants.Count > MaxParticipants)
                {
                    throw new ArgumentException("Minigolf match participant count is out of range", "snapshot");
                }
                for (int p2 = 0; p2 < row.Putts.Count; p2++)
                {
                    MinigolfSnapshot.PuttRow saved = row.Putts[p2];
                    if (saved == null) throw new ArgumentException("Null minigolf putt row", "snapshot");
                    PuttRecord putt = new PuttRecord();
                    putt.Resident = (ResidentId)saved.Resident;
                    putt.Hole = saved.HoleIndex;
                    putt.Turn = saved.TurnIndex;
                    putt.Degrees = saved.Degrees;
                    putt.Power = saved.Power;
                    putt.Draw = saved.HasDraw ? (double?)saved.Draw : null;
                    match.Putts.Add(putt);
                }
                nextMatches[action] = match;
            }
            courses.Clear();
            foreach (KeyValuePair<ObjectId, Course> pair in nextCourses) courses[pair.Key] = pair.Value;
            matches.Clear();
            foreach (KeyValuePair<ActionInstanceId, Match> pair in nextMatches) matches[pair.Key] = pair.Value;

            // The shared match reservation is not an arbitrator queue entry,
            // so SAVE does not replay it; rebuild the same action id here.
            // EnterPerform refuses a second ledger reserve under that id.
            foreach (KeyValuePair<ActionInstanceId, Match> pair in matches)
            {
                Match match = pair.Value;
                if (match.Completed || match.Cancelled) continue;
                if (executor.GetExecution(match.Action) != null) continue;
                ActionEntry entry = new ActionEntry(world.AllocateCommand(StateWriter.SimCore),
                    match.Action, CommandKind.DirectActivity, new ActivityId(ActivityCode),
                    (long)match.CourseId, ActionOwner.Player, CommandSource.PlayerHuman);
                executor.Dispatch(match.Participants[0].Resident, entry);
            }
        }

        private void StepMatch(Match match)
        {
            long now = world.GameSeconds;
            long delta = now - match.LastTick;
            if (delta < 0L) delta = 0L;
            match.LastTick = now;
            if (match.Completed || match.Cancelled) return;
            if (match.CancelRequested || unsafeWeather || !CourseAvailable(match)
                || ParticipantMissing(match) || ConflictingCommand(match))
            {
                CancelMatch(match, now);
                return;
            }
            if (match.Rolling)
            {
                AdvanceBall(match, delta, now);
                if (match.Completed || match.Cancelled) return;
            }
            if (!match.Rolling)
            {
                AdvanceTurn(match, now);
            }
        }

        private void AdvanceTurn(Match match, long now)
        {
            Participant participant = match.Participants[match.ActiveIndex];
            if (participant.Human)
            {
                if (match.HasPending)
                {
                    Launch(match, participant, match.PendingDegrees, match.PendingPower, null);
                }
                return;
            }
            long aim = now - match.TurnStart;
            if (aim < AimGameSeconds)
            {
                return;
            }
            double draw = match.Rng.NextDouble();
            int power = AutonomousPower(match, participant);
            int degrees = AutonomousDegrees(match, participant, draw);
            Launch(match, participant, degrees, power, draw);
        }

        private void Launch(Match match, Participant participant, int degrees, int power,
            double? draw)
        {
            if (match.Completed || match.Cancelled || match.Rolling) return;
            int hole = match.HoleIndex;
            participant.Strokes[hole]++;
            match.HasPending = false;
            match.PendingDegrees = 0;
            match.PendingPower = 0;
            match.AimDegrees = degrees;
            match.AimPower = power;
            match.PuttStart = world.GameSeconds;
            match.PuttRestX = participant.X;
            match.PuttRestY = participant.Y;
            match.PuttRestZ = participant.Z;
            PuttRecord record = new PuttRecord();
            record.Resident = participant.Resident;
            record.Hole = hole;
            record.Turn = match.TurnIndex;
            record.Degrees = degrees;
            record.Power = power;
            record.Draw = draw;
            match.Putts.Add(record);

            double speed = (double)power * match.Course.MetresPerSecondPerPower;
            double radians = (double)degrees * Math.PI / 180.0;
            participant.VelocityX = (float)(Math.Cos(radians) * speed);
            participant.VelocityY = 0.0f;
            participant.VelocityZ = (float)(Math.Sin(radians) * speed);
            if (speed <= 0.0)
            {
                ResolvePutt(match, false, world.GameSeconds);
                return;
            }
            match.Rolling = true;
        }

        private void AdvanceBall(Match match, long delta, long now)
        {
            Participant participant = match.Participants[match.ActiveIndex];
            if (delta <= 0L)
            {
                return;
            }
            long elapsedBefore = match.LastTick - delta - match.PuttStart;
            long remaining = ForcedRestGameSeconds - elapsedBefore;
            if (remaining <= 0L)
            {
                ResolvePutt(match, false, now);
                return;
            }
            double gameLeft = delta < remaining ? (double)delta : (double)remaining;
            bool forced = (double)delta >= (double)remaining;
            while (gameLeft > 0.0)
            {
                double step = gameLeft > SubstepGameSeconds ? SubstepGameSeconds : gameLeft;
                gameLeft -= step;
                double seconds = step / GameSecondsPerNormalSecond;
                double vx = (double)participant.VelocityX;
                double vz = (double)participant.VelocityZ;
                double speed = Math.Sqrt(vx * vx + vz * vz);
                if (speed <= 0.0)
                {
                    ResolvePutt(match, false, now);
                    return;
                }
                double oldX = (double)participant.X;
                double oldZ = (double)participant.Z;
                double distance;
                double nextSpeed;
                if (match.Course.Friction <= 0.0)
                {
                    distance = speed * seconds;
                    nextSpeed = speed;
                }
                else
                {
                    double stopSeconds = speed / match.Course.Friction;
                    if (seconds >= stopSeconds)
                    {
                        distance = speed * stopSeconds * 0.5;
                        nextSpeed = 0.0;
                    }
                    else
                    {
                        distance = speed * seconds - 0.5 * match.Course.Friction * seconds * seconds;
                        nextSpeed = speed - match.Course.Friction * seconds;
                    }
                }
                double nextX = oldX + (vx / speed) * distance;
                double nextZ = oldZ + (vz / speed) * distance;
                Hole hole = match.Course.Holes[match.HoleIndex];
                bool holed = SegmentTouchesCup(oldX, oldZ, nextX, nextZ, hole);
                if (holed)
                {
                    participant.X = (float)nextX;
                    participant.Z = (float)nextZ;
                    participant.VelocityX = 0.0f;
                    participant.VelocityY = 0.0f;
                    participant.VelocityZ = 0.0f;
                    ResolvePutt(match, true, now);
                    return;
                }
                if (nextX < hole.MinX || nextX > hole.MaxX
                    || nextZ < hole.MinZ || nextZ > hole.MaxZ)
                {
                    participant.X = match.PuttRestX;
                    participant.Y = match.PuttRestY;
                    participant.Z = match.PuttRestZ;
                    participant.VelocityX = 0.0f;
                    participant.VelocityY = 0.0f;
                    participant.VelocityZ = 0.0f;
                    ResolvePutt(match, false, now);
                    return;
                }
                participant.X = (float)nextX;
                participant.Z = (float)nextZ;
                if (nextSpeed <= 0.0)
                {
                    participant.VelocityX = 0.0f;
                    participant.VelocityY = 0.0f;
                    participant.VelocityZ = 0.0f;
                    ResolvePutt(match, false, now);
                    return;
                }
                participant.VelocityX = (float)((vx / speed) * nextSpeed);
                participant.VelocityZ = (float)((vz / speed) * nextSpeed);
            }
            if (forced)
            {
                participant.VelocityX = 0.0f;
                participant.VelocityY = 0.0f;
                participant.VelocityZ = 0.0f;
                ResolvePutt(match, false, now);
            }
        }

        private void ResolvePutt(Match match, bool holed, long now)
        {
            match.Rolling = false;
            Participant participant = match.Participants[match.ActiveIndex];
            participant.VelocityX = 0.0f;
            participant.VelocityY = 0.0f;
            participant.VelocityZ = 0.0f;
            int hole = match.HoleIndex;
            if (holed) participant.Complete[hole] = true;
            else if (participant.Strokes[hole] >= MaxStrokes) participant.Complete[hole] = true;
            match.TurnIndex++;
            int count = match.Participants.Count;
            int next = -1;
            for (int offset = 1; offset <= count; offset++)
            {
                int candidate = (match.ActiveIndex + offset) % count;
                if (!match.Participants[candidate].Complete[hole])
                {
                    next = candidate;
                    break;
                }
            }
            if (next >= 0)
            {
                match.ActiveIndex = next;
                BeginTurn(match, now);
                return;
            }
            if (match.HoleIndex < HoleCount - 1)
            {
                match.HoleIndex++;
                match.ActiveIndex = 0;
                for (int i = 0; i < match.Participants.Count; i++)
                {
                    Participant row = match.Participants[i];
                    Hole tee = match.Course.Holes[match.HoleIndex];
                    row.X = tee.Tee.X;
                    row.Y = tee.Tee.Y;
                    row.Z = tee.Tee.Z;
                    row.VelocityX = 0.0f;
                    row.VelocityY = 0.0f;
                    row.VelocityZ = 0.0f;
                }
                BeginTurn(match, now);
                return;
            }
            CompleteMatch(match, now);
        }

        private static void BeginTurn(Match match, long now)
        {
            match.TurnStart = now;
            match.HasPending = false;
            match.PendingDegrees = 0;
            match.PendingPower = 0;
            match.AimDegrees = 0;
            match.AimPower = 0;
        }

        private void CompleteMatch(Match match, long now)
        {
            match.Completed = true;
            match.Rolling = false;
            match.HasPending = false;
            for (int i = 0; i < match.Participants.Count; i++)
            {
                Participant row = match.Participants[i];
                row.VelocityX = 0.0f;
                row.VelocityY = 0.0f;
                row.VelocityZ = 0.0f;
            }
            if (!match.EffectsApplied)
            {
                match.EffectsApplied = true;
                int best = int.MaxValue;
                for (int i = 0; i < match.Participants.Count; i++)
                {
                    Participant row = match.Participants[i];
                    int total = row.Strokes[0] + row.Strokes[1] + row.Strokes[2];
                    if (total < best) best = total;
                }
                for (int i = 0; i < match.Participants.Count; i++)
                {
                    Participant row = match.Participants[i];
                    int total = row.Strokes[0] + row.Strokes[1] + row.Strokes[2];
                    row.Winner = total == best;
                }
                for (int i = 0; i < match.Participants.Count; i++)
                {
                    Participant row = match.Participants[i];
                    LedgerRecipient recipient = new LedgerRecipient(row.Resident);
                    if (Claim(match, FunKind, recipient))
                    {
                        ResidentRecord record;
                        world.TryGetResident(row.Resident, out record);
                        world.SetNeed(Writer, row.Resident, Need.Fun, record.GetNeed(Need.Fun) + 25.0f);
                    }
                    if (Claim(match, SocialKind, recipient))
                    {
                        ResidentRecord record;
                        world.TryGetResident(row.Resident, out record);
                        world.SetNeed(Writer, row.Resident, Need.Social, record.GetNeed(Need.Social) + 10.0f);
                    }
                    if (Claim(match, FitnessKind, recipient))
                    {
                        world.AddSkillPoints(Writer, row.Resident, "Fitness", 5);
                    }
                    if (row.Winner && Claim(match, ProudKind, recipient))
                    {
                        world.AddFeeling(Writer, row.Resident, FeelingKind.Proud, default(EventId),
                            row.Resident, 1, now + Clockwork.GameSecondsPerGameDay);
                    }
                }
                for (int i = 0; i < match.Participants.Count; i++)
                {
                    for (int j = i + 1; j < match.Participants.Count; j++)
                    {
                        Participant first = match.Participants[i];
                        Participant second = match.Participants[j];
                        LedgerRecipient pair = new LedgerRecipient(first.Resident, second.Resident);
                        if (!Claim(match, FriendshipKind, pair)) continue;
                        float friendship;
                        float romance;
                        BondToward(first.Resident, second.Resident, out friendship, out romance);
                        world.SetBond(Writer, first.Resident, second.Resident, friendship + 2.0f, romance);
                        BondToward(second.Resident, first.Resident, out friendship, out romance);
                        world.SetBond(Writer, second.Resident, first.Resident, friendship + 2.0f, romance);
                    }
                }
            }
            ReleaseMatch(match, TerminalOutcome.Completed);
        }

        private void CancelMatch(Match match, long now)
        {
            match.Cancelled = true;
            match.Completed = false;
            match.Rolling = false;
            match.HasPending = false;
            for (int i = 0; i < match.Participants.Count; i++)
            {
                Participant row = match.Participants[i];
                row.Winner = false;
                row.VelocityX = 0.0f;
                row.VelocityY = 0.0f;
                row.VelocityZ = 0.0f;
            }
            if (!match.EffectsApplied)
            {
                match.EffectsApplied = true;
                if (match.Putts.Count > 0)
                {
                    for (int i = 0; i < match.Participants.Count; i++)
                    {
                        Participant row = match.Participants[i];
                        ResidentRecord record;
                        if (!world.TryGetResident(row.Resident, out record) || !record.IsAlive) continue;
                        LedgerRecipient recipient = new LedgerRecipient(row.Resident);
                        if (Claim(match, FunKind, recipient))
                        {
                            world.SetNeed(Writer, row.Resident, Need.Fun, record.GetNeed(Need.Fun) + 10.0f);
                        }
                    }
                }
            }
            ReleaseMatch(match, TerminalOutcome.Interrupted);
        }

        private bool Claim(Match match, string kind, LedgerRecipient recipient)
        {
            if (!world.ReserveEffectKeyed(Writer, match.Action, kind, recipient)) return false;
            world.CommitEffectKeyed(Writer, match.Action, kind, recipient);
            return true;
        }

        private void ReleaseMatch(Match match, TerminalOutcome outcome)
        {
            if (outcome == TerminalOutcome.Completed)
            {
                world.CommitEffectKeyed(Writer, match.Action, ActivityCode,
                    new LedgerRecipient(match.Participants[0].Resident));
            }
            executor.TerminateAction(match.Action, outcome);
        }

        // Entry/cancellation takes the participant's queue over, so only the
        // match itself remains between ticks.
        private void TakeOver(ResidentId resident)
        {
            ActionEntry current = arbitrator.GetCurrent(resident);
            if (current != null) SubmitCancelCurrent(resident);
            ActionEntry[] pending = arbitrator.GetPending(resident);
            for (int i = pending.Length - 1; i >= 0; i--)
            {
                CancelPending(resident, pending[i].Command);
            }
        }

        private void SubmitCancelCurrent(ResidentId resident)
        {
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record)) return;
            CommandEnvelope envelope = new CommandEnvelope(
                world.AllocateCommand(StateWriter.SimCore), world.Epoch, world.GameSeconds,
                CommandSource.PlayerHuman, record.ResidentSequence + 1u,
                CommandKind.CancelCurrent, resident, null, null,
                CommandParameters.None.Instance);
            arbitrator.Submit(envelope);
        }

        private void CancelPending(ResidentId resident, CommandId command)
        {
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record)) return;
            CommandEnvelope envelope = new CommandEnvelope(
                world.AllocateCommand(StateWriter.SimCore), world.Epoch, world.GameSeconds,
                CommandSource.PlayerHuman, record.ResidentSequence + 1u,
                CommandKind.CancelQueued, resident, null, (long)command,
                CommandParameters.None.Instance);
            arbitrator.Submit(envelope);
        }

        private bool CourseAvailable(Match match)
        {
            return executor.IsObjectRegistered(match.CourseId);
        }

        private bool ParticipantMissing(Match match)
        {
            for (int i = 0; i < match.Participants.Count; i++)
            {
                ResidentRecord record;
                if (!world.TryGetResident(match.Participants[i].Resident, out record)
                    || !record.IsAlive)
                {
                    return true;
                }
            }
            return false;
        }

        private bool ConflictingCommand(Match match)
        {
            for (int i = 0; i < match.Participants.Count; i++)
            {
                Participant row = match.Participants[i];
                if (arbitrator.GetCurrent(row.Resident) != null) return true;
                if (arbitrator.GetPending(row.Resident).Length > 0) return true;
            }
            return false;
        }

        private int AutonomousPower(Match match, Participant participant)
        {
            Hole hole = match.Course.Holes[match.HoleIndex];
            double dx = (double)hole.Cup.X - (double)participant.X;
            double dz = (double)hole.Cup.Z - (double)participant.Z;
            double distance = Math.Sqrt(dx * dx + dz * dz);
            int power = (int)Math.Round(distance * 10.0 / 5.0) * 5;
            if (power < 5) power = 5;
            if (power > 100) power = 100;
            return power;
        }

        private int AutonomousDegrees(Match match, Participant participant, double draw)
        {
            Hole hole = match.Course.Holes[match.HoleIndex];
            double dx = (double)hole.Cup.X - (double)participant.X;
            double dz = (double)hole.Cup.Z - (double)participant.Z;
            double baseDegrees = Math.Atan2(dz, dx) * 180.0 / Math.PI;
            int fitness = FitnessLevel(participant.Resident);
            double error = (draw * 2.0 - 1.0) * (10.0 - (double)fitness);
            int degrees = (int)Math.Round((baseDegrees + error) / 5.0) * 5;
            degrees %= 360;
            if (degrees < 0) degrees += 360;
            return degrees;
        }

        private int FitnessLevel(ResidentId resident)
        {
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record)) return 0;
            for (int i = 0; i < record.Skills.Count; i++)
            {
                if (string.Equals(record.Skills[i].SkillId, "Fitness", StringComparison.Ordinal))
                {
                    return record.Skills[i].Level;
                }
            }
            return 0;
        }

        private void BondToward(ResidentId resident, ResidentId other,
            out float friendship, out float romance)
        {
            friendship = 0.0f;
            romance = 0.0f;
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record)) return;
            for (int i = 0; i < record.Bonds.Count; i++)
            {
                if (record.Bonds[i].Other == other)
                {
                    friendship = record.Bonds[i].Friendship;
                    romance = record.Bonds[i].Romance;
                    return;
                }
            }
        }

        private static int FindParticipant(Match match, ResidentId resident)
        {
            for (int i = 0; i < match.Participants.Count; i++)
            {
                if (match.Participants[i].Resident == resident) return i;
            }
            return -1;
        }

        private static bool SegmentTouchesCup(double x0, double z0, double x1, double z1, Hole hole)
        {
            double sx = x1 - x0;
            double sz = z1 - z0;
            double length = sx * sx + sz * sz;
            double t = 0.0;
            if (length > 0.0)
            {
                t = (((double)hole.Cup.X - x0) * sx + ((double)hole.Cup.Z - z0) * sz) / length;
                if (t < 0.0) t = 0.0;
                if (t > 1.0) t = 1.0;
            }
            double cx = x0 + sx * t;
            double cz = z0 + sz * t;
            double dx = (double)hole.Cup.X - cx;
            double dz = (double)hole.Cup.Z - cz;
            return dx * dx + dz * dz <= hole.CupRadius * hole.CupRadius;
        }

        private static long SeedFor(long worldSeed, ActionInstanceId action)
        {
            unchecked
            {
                return (long)((ulong)worldSeed ^ ((ulong)(long)action * 2685821657736338717ul));
            }
        }

        private static Hole[] CopyHoles(Hole[] holes)
        {
            Hole[] copy = new Hole[holes.Length];
            Array.Copy(holes, copy, holes.Length);
            return copy;
        }

        private static void SortParticipants(List<Participant> rows)
        {
            for (int i = 1; i < rows.Count; i++)
            {
                Participant key = rows[i];
                int j = i - 1;
                while (j >= 0 && (long)rows[j].Resident > (long)key.Resident)
                {
                    rows[j + 1] = rows[j];
                    j--;
                }
                rows[j + 1] = key;
            }
        }

        private static void SortObjects(List<ObjectId> rows)
        {
            for (int i = 1; i < rows.Count; i++)
            {
                ObjectId key = rows[i];
                int j = i - 1;
                while (j >= 0 && (long)rows[j] > (long)key) { rows[j + 1] = rows[j]; j--; }
                rows[j + 1] = key;
            }
        }

        private static void SortActions(List<ActionInstanceId> rows)
        {
            for (int i = 1; i < rows.Count; i++)
            {
                ActionInstanceId key = rows[i];
                int j = i - 1;
                while (j >= 0 && (long)rows[j] > (long)key) { rows[j + 1] = rows[j]; j--; }
                rows[j + 1] = key;
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static void CheckWriter(StateWriter writer)
        {
            if (writer != StateWriter.SimCore && writer != StateWriter.CommandArbitrator
                && writer != StateWriter.ActivityExecutor)
            {
                throw new ArgumentException("Unknown state writer", "writer");
            }
        }

        private static MatchSnapshot Detach(Match match)
        {
            MatchSnapshot snapshot = new MatchSnapshot();
            snapshot.Action = match.Action;
            snapshot.HoleIndex = match.HoleIndex;
            snapshot.TurnIndex = match.TurnIndex;
            snapshot.ActiveResident = match.Participants.Count == 0
                ? default(ResidentId)
                : match.Participants[match.ActiveIndex].Resident;
            snapshot.Rolling = match.Rolling;
            snapshot.Completed = match.Completed;
            snapshot.Cancelled = match.Cancelled;
            snapshot.ElapsedGameSeconds = match.LastTick - match.StartedAt;
            snapshot.PuttElapsedGameSeconds = match.Rolling
                ? match.LastTick - match.PuttStart : 0L;
            snapshot.AimElapsedGameSeconds = match.LastTick - match.TurnStart;
            snapshot.AimDegrees = match.AimDegrees;
            snapshot.AimPower = match.AimPower;
            snapshot.PendingConfirmationCount = match.HasPending ? 1 : 0;
            snapshot.RandomState = match.Rng.State;
            snapshot.Participants = new ParticipantSnapshot[match.Participants.Count];
            for (int i = 0; i < match.Participants.Count; i++)
            {
                Participant participant = match.Participants[i];
                ParticipantSnapshot row = new ParticipantSnapshot();
                row.Resident = participant.Resident;
                row.Human = participant.Human;
                row.Winner = participant.Winner;
                for (int h = 0; h < HoleCount; h++)
                {
                    row.Strokes[h] = participant.Strokes[h];
                    row.HoleComplete[h] = participant.Complete[h];
                }
                row.BallPosition = new ApproachPose(participant.X, participant.Y, participant.Z);
                row.BallVelocity = new ApproachPose(
                    participant.VelocityX, participant.VelocityY, participant.VelocityZ);
                snapshot.Participants[i] = row;
            }
            snapshot.Putts = new PuttSnapshot[match.Putts.Count];
            for (int i = 0; i < match.Putts.Count; i++)
            {
                PuttRecord putt = match.Putts[i];
                PuttSnapshot row = new PuttSnapshot();
                row.Resident = putt.Resident;
                row.HoleIndex = putt.Hole;
                row.TurnIndex = putt.Turn;
                row.Degrees = putt.Degrees;
                row.Power = putt.Power;
                row.RandomDraw = putt.Draw;
                snapshot.Putts[i] = row;
            }
            return snapshot;
        }
    }
}
