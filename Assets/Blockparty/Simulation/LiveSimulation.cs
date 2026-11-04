// REFINE@1: the ordinary live-play composition of the simulation. One shared
// GameStack plus the life services the evidence worlds arm, ticked through one
// real clock entry point. Plain C# 9, no UnityEngine dependency.
// PopulationScheduler integrates needs per fixed tick; the remaining services
// run at the clock boundary exactly as AlphaDirector.RunLifeGates drives them.
using System;
using System.Collections.Generic;
using System.IO;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Persistence;

namespace Blockparty.Simulation
{
    public sealed class LiveSimulation
    {
        private const StateWriter Core = StateWriter.SimCore;
        private const StateWriter Exec = StateWriter.ActivityExecutor;
        private const long Day = 86400L;
        private const long LiveWorldId = 94L;
        private const long SettleGameSeconds = 1500L;

        private static readonly string[] Names =
        {
            "Alex", "Bea", "Cara", "Dev", "Eli", "Fran", "Gio", "Harper",
        };

        private sealed class Tuning
        {
            public SimulationSpeed Speed;
            public AgingPreset Preset;
            public long InitialGameSeconds;
        }

        private GameStack game;
        private WorldState world;
        private Clock clock;
        private CommandArbitrator commands;
        private ActivityExecutor executor;
        private AppearanceStore appearances;
        private Accounts accounts;
        private Food food;
        private Expenses expenses;
        private Ownership ownership;
        private Home home;
        private Club club;
        private Housing housing;
        private Creative creative;
        private FamilyCreation family;
        private BlockSites sites;
        private Work work;
        private Education education;
        private Social social;
        private Minigolf minigolf;
        private Health health;
        private Progression progression;
        private Autonomy autonomy;
        private PopulationScheduler scheduler;
        private Aging aging;
        private EstateCare estate;
        private SevereStorm storm;
        private TierContext context;
        private int frame;
        private long settledGameSeconds;
        private ObjectId bed;
        private ApproachPose bedPose;
        private ObjectId[] commonObjects;
        private ObjectId roof;
        private ObjectId electric;
        private ObjectId outdoor;
        private ObjectId shelter;
        // shortcut: paused moves live here, not in the save; a reload drops them. Upgrade if BP-SAVE needs them.
        private readonly Dictionary<ActionInstanceId, ResidentId> bathroom = new Dictionary<ActionInstanceId, ResidentId>();
        private readonly List<KeyValuePair<HouseholdId, HomeId>> pausedMoves = new List<KeyValuePair<HouseholdId, HomeId>>();
        // M5-OD-52: live club attendances in flight and the open meeting (attendees whose perform spans overlap).
        private readonly Dictionary<ActionInstanceId, ResidentId> clubbing = new Dictionary<ActionInstanceId, ResidentId>();
        private readonly List<ResidentId> meeting = new List<ResidentId>();
        // Members of the open meeting a save already paid; later co-attendees still bond with them.
        private readonly List<ResidentId> meetingPaid = new List<ResidentId>();
        private long meetingEnd = -1L;
        // The open meeting as last saved; a Rebind to that save (same world id and clock) restores it.
        private ResidentId[] savedPaid = new ResidentId[0];
        private long savedEnd = -1L, savedWorld = -1L, savedAt = -1L;
        // Club.ClubMeeting's per-pair Friendship gain, for co-attendees paid across a save.
        private const float MeetingFriendship = 3.0f;
        // Attendee lists of paid meetings, oldest first; the live director drains it to log and toast them.
        public readonly List<ResidentId[]> PaidMeetings = new List<ResidentId[]>();

        // The one live club: every teen+ starting resident is a member, so co-attendees exist.
        public const string LiveClub = "GardenClub";
        private const string ClubCode = "ACT-CLUB-01";
        // M5-OD-49: a resident within this radius (metres) is a social partner even with no bond yet.
        private const double PartnerRadiusMeters = 25.0;

        public GameStack Game { get { return game; } }
        public BlockSites Sites { get { return sites; } }
        public Work Work { get { return work; } }
        public Education Education { get { return education; } }
        public Social Social { get { return social; } }
        public Minigolf Minigolf { get { return minigolf; } }
        public Health Health { get { return health; } }
        public Progression Progression { get { return progression; } }
        public Autonomy Autonomy { get { return autonomy; } }
        private bool freeWill = true;
        // Survives world load and new game: each rebuilt Autonomy inherits it.
        public bool FreeWill { get { return freeWill; } set { freeWill = value; if (autonomy != null) autonomy.FreeWill = value; } }
        public Aging Aging { get { return aging; } }
        public EstateCare Estate { get { return estate; } }
        public SevereStorm Storm { get { return storm; } }
        public ObjectId Bed { get { return bed; } }
        public ObjectId[] CommonObjects { get { return commonObjects; } }
        public ObjectId Roof { get { return roof; } }
        public ObjectId Electric { get { return electric; } }
        public ObjectId Outdoor { get { return outdoor; } }
        public ObjectId Shelter { get { return shelter; } }

        public static LiveSimulation Create(long seed, string tuningPath)
        {
            if (tuningPath == null)
            {
                throw new ArgumentNullException("tuningPath");
            }
            Tuning tuning = ReadTuning(tuningPath);
            LiveSimulation live = new LiveSimulation();
            live.Build(seed, tuning);
            return live;
        }

        // Rebind after SaveV1.LoadGame: same entry point, same live services,
        // new shared GameStack. Site objects and their ids stay with the scene.
        public void Rebind(LoadedGame loaded)
        {
            if (loaded == null)
            {
                throw new ArgumentNullException("loaded");
            }
            Rebind(new GameStack(loaded.World, loaded.Clock, loaded.Arbitrator, loaded.Executor,
                loaded.Appearances, loaded.Accounts, loaded.Food, loaded.Expenses, loaded.Ownership,
                loaded.Home, loaded.Club, loaded.Housing, loaded.Creative, loaded.Family));
        }

        public void Rebind(GameStack stack)
        {
            if (stack == null)
            {
                throw new ArgumentNullException("stack");
            }
            game = stack;
            world = stack.World;
            clock = stack.Clock;
            commands = stack.Arbitrator;
            executor = stack.Executor;
            appearances = stack.Appearances;
            accounts = stack.Accounts;
            food = stack.Food;
            expenses = stack.Expenses;
            ownership = stack.Ownership;
            home = stack.Home;
            club = stack.Club;
            housing = stack.Housing;
            creative = stack.Creative;
            family = stack.Family;
            health = new Health(world, commands, executor);
            social = new Blockparty.Activities.Social(world, commands, executor);
            work = new Work(world, commands, executor, accounts, sites);
            education = new Education(world, commands, executor, accounts, sites);
            minigolf = executor.MinigolfService ?? new Minigolf(world, commands, executor, clock);
            progression = new Progression(world);
            autonomy = new Autonomy(world, commands) { FreeWill = freeWill };
            aging = new Aging(world, clock);
            estate = new EstateCare(game);
            storm = new SevereStorm(world, executor, home, club);
            scheduler = new PopulationScheduler(world, clock, commands, executor, autonomy);
            scheduler.Offered = Offered;
            frame = 0;
            settledGameSeconds = world.GameSeconds;
            pausedMoves.Clear();
            bathroom.Clear();
            critical.Clear();
            seenEvents = world.Events.Count;
            clubbing.Clear();
            meeting.Clear();
            meetingPaid.Clear();
            meetingEnd = -1L;
            if (world.WorldId == savedWorld && world.GameSeconds == savedAt)
            {
                meetingPaid.AddRange(savedPaid);
                meetingEnd = savedEnd;
            }
            // Restored actions may finish inside the first resumed Settle, before the per-frame tracking runs.
            TrackActions();
        }

        // Live snapshot: settle the pending need interval first so the save
        // carries it (Rebind re-anchors at the saved clock).
        public SaveWriteResult Save(string directory)
        {
            // An open meeting's finished attendees are paid now (their actions left the queue); the
            // meeting stays open so attendees still performing bond with them when they finish.
            PayAttended();
            scheduler.SettleNeeds(Core, settledGameSeconds);
            settledGameSeconds = world.GameSeconds;
            savedPaid = meetingPaid.ToArray();
            savedEnd = meetingEnd;
            savedWorld = world.WorldId;
            savedAt = world.GameSeconds;
            return SaveV1.SaveGame(game, directory);
        }

        // Scene-side registrations the save does not own: the authored bed and
        // the storm objects stay reachable after a reload.
        public void RegisterSceneObjects(ActivityExecutor target)
        {
            if (target == null)
            {
                throw new ArgumentNullException("target");
            }
            target.RegisterObject(bed, new[] { bedPose, bedPose });
            for (int i = 0; i < commonObjects.Length; i++)
            {
                target.RegisterObject(commonObjects[i], new[] { new ApproachPose(4f + i, 0f, 4f) });
            }
            target.RegisterObject(roof, new[] { new ApproachPose(-16f, 0f, 2f) });
            target.RegisterObject(electric, new[] { new ApproachPose(8f, 0f, 8f) });
            target.RegisterObject(outdoor, new[] { new ApproachPose(10f, 0f, 10f) });
            target.RegisterObject(shelter, new[] { new ApproachPose(8f, 0f, 4f) });
        }

        // One real clock advance per frame; the need line settles at a live
        // cadence (linear decay is exact across the chunk) and the remaining
        // services tick at the boundary while the clock stands still.
        public void AdvanceRealSeconds(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0.0)
            {
                throw new ArgumentOutOfRangeException("seconds", "Advance amount must be finite and non-negative");
            }
            long before = clock.TotalTicks;
            clock.AdvanceRealSeconds(Core, seconds);
            if (clock.TotalTicks == before)
            {
                return;
            }
            frame++;
            // BP-TIME-02: moves accepted while paused execute at this first unpaused boundary.
            for (int i = 0; i < pausedMoves.Count; i++)
            {
                housing.MoveHousehold(Core, pausedMoves[i].Key, pausedMoves[i].Value);
            }
            pausedMoves.Clear();
            // Game-time cadence (50 Normal frames): Ultra settles as often per game hour, so autonomy reads current needs.
            if (frame == 1 || world.GameSeconds - settledGameSeconds >= SettleGameSeconds)
            {
                scheduler.Settle(Core, settledGameSeconds, context);
                settledGameSeconds = world.GameSeconds;
            }
            scheduler.PlanIdle(context);
            PlanAutonomousParties();
            TrackActions();
            housing.Tick(Core);
            work.Tick(Exec);
            education.Tick(Exec);
            social.Tick(Exec);
            home.Tick(Exec);
            minigolf.Tick(Exec);
            game.Party.Tick(Exec);
            TickParties();
            family.Tick(Exec);
            progression.Tick(Exec);
            health.Tick(Exec);
            aging.Tick(Core);
            estate.Tick(Exec);
            storm.Tick(Core);
            ApplyBathroomCompletions();
            ApplyClubCompletions();
            WatchSlowdownEvents();
        }

        // SDoc section 5 completion effects no live service owns: HygieneRoutine
        // sets Hygiene and Bathroom sets Bladder to 100, once, on natural completion.
        // shortcut: live targets carry no shower/sink kind, so HygieneRoutine takes the shower value; upgrade when sinks exist.
        private void TrackActions()
        {
            ResidentRecord[] all = world.ResidentsSnapshot();
            for (int i = 0; i < all.Length; i++)
            {
                ActionEntry current = commands.GetCurrent(all[i].Id);
                if (current != null) Track(current, all[i].Id);
                foreach (ActionEntry pending in commands.GetPending(all[i].Id)) Track(pending, all[i].Id);
            }
        }

        private void Track(ActionEntry entry, ResidentId resident)
        {
            if (entry.Kind != CommandKind.DirectActivity) return;
            if (BathroomNeed(entry.Activity.Code).HasValue) bathroom[entry.Action] = resident;
            if (entry.Activity.Code == ClubCode) clubbing[entry.Action] = resident;
        }

        // M5-OD-52: ACT-CLUB-01 completions whose perform spans overlap are one meeting.
        // Club.ClubMeeting pays it once, with every attendee, when no overlapping
        // attendance is still performing; interrupted attendances pay nothing.
        private void ApplyClubCompletions()
        {
            List<ActionInstanceId> finished = new List<ActionInstanceId>();
            foreach (KeyValuePair<ActionInstanceId, ResidentId> pair in clubbing)
            {
                ExecutionRecord done = executor.GetExecution(pair.Key);
                if (done == null || !done.IsTerminal) continue;
                finished.Add(pair.Key);
                if (done.Outcome != TerminalOutcome.Completed) continue;
                if (meeting.Count + meetingPaid.Count > 0 && done.PerformStartedAtGameSeconds > meetingEnd) PayMeeting();
                if (!meeting.Contains(pair.Value) && !meetingPaid.Contains(pair.Value)) meeting.Add(pair.Value);
                meetingEnd = Math.Max(meetingEnd, world.GameSeconds);
            }
            for (int i = 0; i < finished.Count; i++) clubbing.Remove(finished[i]);
            if (meeting.Count + meetingPaid.Count == 0) return;
            foreach (ActionInstanceId action in clubbing.Keys)
            {
                ExecutionRecord running = executor.GetExecution(action);
                if (running != null && running.PerformStartedAtGameSeconds >= 0L
                    && running.PerformStartedAtGameSeconds <= meetingEnd) return;
            }
            PayMeeting();
        }

        // Close the open meeting: pay its unpaid attendees, then report every paid member once.
        private void PayMeeting()
        {
            PayAttended();
            if (meetingPaid.Count > 0) PaidMeetings.Add(meetingPaid.ToArray());
            meetingPaid.Clear();
            meetingEnd = -1L;
        }

        // Pay the finished unpaid attendees once (Club.ClubMeeting: standing, Social, Friendship
        // among them), plus the meeting Friendship with the members an earlier save paid.
        private void PayAttended()
        {
            if (meeting.Count == 0) return;
            int[] before = new int[meeting.Count];
            for (int i = 0; i < meeting.Count; i++) before[i] = club.Standing(LiveClub, meeting[i]);
            club.ClubMeeting(Exec, LiveClub, meeting.ToArray());
            int paid = meetingPaid.Count;
            for (int i = 0; i < meeting.Count; i++)
            {
                // ClubMeeting skips non-members; a standing rise marks the members it paid.
                if (club.Standing(LiveClub, meeting[i]) == before[i]) continue;
                for (int j = 0; j < paid; j++)
                {
                    AddFriendship(meeting[i], meetingPaid[j]);
                    AddFriendship(meetingPaid[j], meeting[i]);
                }
                meetingPaid.Add(meeting[i]);
            }
            meeting.Clear();
        }

        private void AddFriendship(ResidentId self, ResidentId other)
        {
            ResidentRecord record;
            if (!world.TryGetResident(self, out record)) return;
            float friendship = 0f, romance = 0f;
            for (int b = 0; b < record.Bonds.Count; b++)
            {
                if (record.Bonds[b].Other == other) { friendship = record.Bonds[b].Friendship; romance = record.Bonds[b].Romance; }
            }
            world.SetBond(Exec, self, other, friendship + MeetingFriendship, romance);
        }

        private void ApplyBathroomCompletions()
        {
            List<ActionInstanceId> finished = new List<ActionInstanceId>();
            foreach (KeyValuePair<ActionInstanceId, ResidentId> pair in bathroom)
            {
                ExecutionRecord done = executor.GetExecution(pair.Key);
                if (done == null || !done.IsTerminal) continue;
                finished.Add(pair.Key);
                ResidentRecord record;
                if (done.Outcome == TerminalOutcome.Completed && world.TryGetResident(pair.Value, out record) && record.IsAlive)
                {
                    world.SetNeed(Exec, pair.Value, BathroomNeed(done.Activity.Code).Value, StateSchema.NeedMax);
                }
            }
            for (int i = 0; i < finished.Count; i++) bathroom.Remove(finished[i]);
        }

        // Autonomy never picks an action live play cannot finish: food the live
        // Food service would refuse (section 5 stock and cost rules), so an
        // empty pantry cannot pin a hungry resident to failing remedies, a
        // bare minigolf, which only a Minigolf match ends, and a party
        // invitation or guestless party, which no live attendance pays.
        private IReadOnlyList<AvailableAction> Offered(ResidentId resident)
        {
            ResidentRecord record;
            world.TryGetResident(resident, out record);
            List<AvailableAction> offered = new List<AvailableAction>();
            List<ResidentId> partners = Partners(record);
            foreach (AvailableAction option in commands.Snapshot(resident).Available)
            {
                if (!Startable(option.Activity.Code, record)) continue;
                // M5-OD-49: Chat and FriendlyActivity name their partner, one option per candidate, so
                // Autonomy's SocialFactor/HistoryFactor rank partners and Social applies the outcome.
                if (option.Activity.Code == "ACT-REL-01" || option.Activity.Code == "ACT-REL-02")
                {
                    for (int i = 0; i < partners.Count; i++) offered.Add(new AvailableAction(option.Activity, (long)partners[i]));
                }
                else offered.Add(option);
            }
            // M5-OD-35: Autonomy remedies the first critical need in enum order (Energy before
            // Bladder) and Rest's Comfort gain outscores Sleep, so a critical Bladder takes the
            // Bathroom and a critical Energy takes Sleep, never Rest.
            if (record.GetNeed(Need.Bladder) <= CriticalNeed && offered.Exists(o => o.Activity.Code == "ACT-HEALTH-04"))
            {
                return offered.FindAll(o => o.Activity.Code == "ACT-HEALTH-04");
            }
            if (record.GetNeed(Need.Energy) <= CriticalNeed && offered.Exists(o => o.Activity.Code == "ACT-HEALTH-01"))
            {
                offered.RemoveAll(o => o.Activity.Code == "ACT-HEALTH-02");
            }
            return offered;
        }

        // Living residents nearby or already bonded with the chooser.
        private List<ResidentId> Partners(ResidentRecord self)
        {
            List<ResidentId> partners = new List<ResidentId>();
            ApproachPose here = executor.GetResidentPosition(self.Id);
            ResidentRecord[] all = world.ResidentsSnapshot();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Id == self.Id || !all[i].IsAlive) continue;
                ApproachPose there = executor.GetResidentPosition(all[i].Id);
                double dx = there.X - here.X, dz = there.Z - here.Z;
                bool bonded = false;
                for (int b = 0; b < self.Bonds.Count; b++) bonded |= self.Bonds[b].Other == all[i].Id;
                if (bonded || dx * dx + dz * dz <= PartnerRadiusMeters * PartnerRadiusMeters) partners.Add(all[i].Id);
            }
            return partners;
        }

        // Autonomy's urgent-remedy threshold (SDoc section 5 x4 urgency band).
        private const float CriticalNeed = 10.0f;

        private bool Startable(string code, ResidentRecord record)
        {
            switch (code)
            {
                case "ACT-FOOD-01": return food.GroceryUnits(record.Home) > 0;
                case "ACT-FOOD-02": return food.PreparedMeals(record.Home) > 0;
                case "ACT-FOOD-03": return record.Money >= 25L;
                case "ACT-FOOD-04": return record.Stage >= LifeStage.Teen && record.Money >= 18L;
                case "ACT-SPORT-02": return false;
                // Invitations come only from a host's plan, never as a bare autonomous pick.
                case "ACT-PARTY-02": return false;
                // An autonomous pick becomes a dated plan with invites (PlanAutonomousParties).
                case "ACT-PARTY-01": return CanPlanParty(record.Id);
                default: return true;
            }
        }

        private static Need? BathroomNeed(string code)
        {
            if (code == "ACT-HEALTH-03") return Need.Hygiene;
            if (code == "ACT-HEALTH-04") return Need.Bladder;
            return null;
        }

        public CommandResult Submit(string commandId, ResidentId actor, long target)
        {
            if (commandId == null)
            {
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            ResidentRecord record;
            if (!world.TryGetResident(actor, out record))
            {
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            if (!record.IsAlive)
            {
                return CommandResult.Reject(RejectReason.ResidentDeceased);
            }
            if (string.Equals(commandId, "live.conceive", StringComparison.Ordinal))
            {
                ResidentId partner;
                if (!ConsentPartner(actor, out partner) || (long)partner != target)
                {
                    return CommandResult.Reject(RejectReason.InvalidParameters);
                }
                ObjectId authoredBed;
                if (!BedFor(record.Home, out authoredBed))
                {
                    return CommandResult.Reject(RejectReason.TargetMissing);
                }
                return family.Submit(Envelope(actor, "ACT-REL-06", (long)authoredBed));
            }
            if (string.Equals(commandId, "live.move-household", StringComparison.Ordinal))
            {
                if (target <= 0L)
                {
                    return CommandResult.Reject(RejectReason.InvalidParameters);
                }
                if (!clock.Paused)
                {
                    return housing.MoveHousehold(Core, record.Household, (HomeId)target);
                }
                if (!housing.GetOffer(record.Household, (HomeId)target, AccountRef.Shared(record.Household)).Fits)
                {
                    return CommandResult.Reject(RejectReason.InvalidParameters);
                }
                pausedMoves.Add(new KeyValuePair<HouseholdId, HomeId>(record.Household, (HomeId)target));
                return CommandResult.Accept();
            }
            if (string.Equals(commandId, "ACT-HEALTH-02", StringComparison.Ordinal))
            {
                return health.Submit(Envelope(actor, commandId, target == 0L ? (long?)null : target));
            }
            return CommandResult.Reject(RejectReason.UnknownActivity);
        }

        // The minigolf match is one executor reservation, not a queue entry, so
        // the HUD cancel never sees it; Cancel takes the match's own Leave path.
        // Searching the snapshot also finds a match restored by a reload.
        public bool CancelMinigolf(ResidentId resident)
        {
            foreach (Minigolf.MinigolfSnapshot.MatchRow row in minigolf.Export().Matches)
            {
                if (row.Completed || row.Cancelled) continue;
                foreach (Minigolf.MinigolfSnapshot.ParticipantRow participant in row.Participants)
                {
                    if (participant.Resident != (long)resident) continue;
                    minigolf.Leave((ActionInstanceId)row.Action, resident);
                    return true;
                }
            }
            return false;
        }

        // The consenting partner the conception command targets. The live UI
        // uses it to offer the ordinary command without inventing a queue.
        public bool ConsentPartner(ResidentId resident, out ResidentId partner)
        {
            partner = default(ResidentId);
            FamilyCreation.FamilySnapshot snapshot = family.Export();
            for (int i = 0; i < snapshot.Partners.Count; i++)
            {
                if ((long)snapshot.Partners[i].Initiator == (long)resident && snapshot.Partners[i].Consent)
                {
                    partner = (ResidentId)snapshot.Partners[i].Partner;
                    return true;
                }
            }
            return false;
        }

        // First vacant home with enough beds for the actor's household.
        public bool VacantHomeFor(ResidentId resident, out HomeId destination)
        {
            destination = default(HomeId);
            ResidentRecord actor;
            if (!world.TryGetResident(resident, out actor))
            {
                return false;
            }
            int members = 0;
            ResidentRecord[] all = world.ResidentsSnapshot();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Household == actor.Household) members++;
            }
            Housing.HousingSnapshot snapshot = housing.Export();
            for (int i = 0; i < snapshot.Homes.Count; i++)
            {
                HomeId candidate = (HomeId)snapshot.Homes[i].Home;
                bool occupied = false;
                for (int r = 0; r < all.Length; r++)
                {
                    if (all[r].Home == candidate) occupied = true;
                }
                HomeRecord row;
                if (!occupied && world.TryGetHome(candidate, out row) && row.BedCount >= members)
                {
                    destination = candidate;
                    return true;
                }
            }
            return false;
        }

        private void Build(long seed, Tuning tuning)
        {
            world = ResidentFactory.CreateNewGame(LiveWorldId, seed, Names, out appearances);
            clock = new Clock(world);
            clock.SetSpeed(tuning.Speed);
            clock.SetPreset(tuning.Preset);
            world.SetGameSeconds(Core, tuning.InitialGameSeconds);

            commands = new CommandArbitrator(world, clock);
            executor = new ActivityExecutor(world, commands, clock);
            accounts = new Accounts(world);
            food = new Food(world, commands, executor, accounts);
            expenses = new Expenses(world, accounts);
            ownership = new Ownership(world, accounts);
            housing = new Housing(world, accounts, ownership, executor, appearances);
            home = new Home(world, commands, executor, accounts);
            club = new Club(world);
            creative = new Creative(world);
            family = new FamilyCreation(world, commands, executor, appearances, housing);
            // Object ids must stay clear of resident ids, live arrivals and births
            // included (the executor resolves both); BlockSites allocates its own.
            ObjectId next;
            do { next = world.AllocateObject(Core); } while ((long)next <= Names.Length + 40L);
            sites = new BlockSites(world, executor);
            work = new Work(world, commands, executor, accounts, sites);
            education = new Education(world, commands, executor, accounts, sites);
            social = new Social(world, commands, executor);
            minigolf = new Minigolf(world, commands, executor, clock);
            health = new Health(world, commands, executor);
            progression = new Progression(world);
            autonomy = new Autonomy(world, commands) { FreeWill = freeWill };
            game = new GameStack(world, clock, commands, executor, appearances, accounts,
                food, expenses, ownership, home, club, housing, creative, family);

            ResidentRecord[] rows = world.ResidentsSnapshot();
            Array.Sort(rows, CompareResidents);
            ResidentId carrier = rows[0].Id;
            ResidentId other = rows[1].Id;
            ResidentId mover = rows[2].Id;
            ResidentId roommate = rows[3].Id;
            ResidentId elder = rows[4].Id;
            ResidentId dependent = rows[5].Id;
            ResidentId exposed = rows[6].Id;
            ResidentId growing = rows[7].Id;

            // Four authored households plus nine vacant homes: the real
            // HOUSING vacancy path fills the population gate while the live
            // player moves one household into an authored vacant home.
            HomeRecord first = world.ExportSnapshot().Homes[0];
            HouseholdRecord firstHousehold = world.ExportSnapshot().Households[0];
            HomeRecord[] homes = new HomeRecord[14];
            homes[0] = first;
            for (int i = 1; i < homes.Length; i++)
            {
                homes[i] = world.AllocateHome(Core, 8);
            }
            HouseholdRecord[] households = new HouseholdRecord[5];
            households[0] = firstHousehold;
            for (int i = 1; i < households.Length; i++)
            {
                households[i] = world.AllocateHousehold(Core, "Live household " + (i + 1), homes[i].Id);
            }
            for (int i = 0; i < homes.Length; i++)
            {
                housing.RegisterHome(Core, homes[i].Id, 0L);
            }
            world.SetHousehold(Core, carrier, households[0].Id, homes[0].Id);
            world.SetHousehold(Core, other, households[1].Id, homes[1].Id);
            world.SetHousehold(Core, mover, households[2].Id, homes[2].Id);
            world.SetHousehold(Core, roommate, households[2].Id, homes[2].Id);
            world.SetHousehold(Core, growing, households[2].Id, homes[2].Id);
            world.SetHousehold(Core, elder, households[3].Id, homes[3].Id);
            world.SetHousehold(Core, dependent, households[3].Id, homes[3].Id);
            world.SetHousehold(Core, exposed, households[4].Id, homes[4].Id);

            // Mixed ages near the authored thresholds: a birthday, an elder
            // death with a dependent care transfer, and live conception.
            world.SetStage(Core, growing, LifeStage.Child, 31.4f);
            world.SetAging(Core, growing, 31.4, 0.0, false);
            world.SetStage(Core, elder, LifeStage.Elder, 31.4f);
            world.SetAging(Core, elder, 31.4, 31.5, false);
            world.SetStage(Core, dependent, LifeStage.Toddler, 15.7f);
            world.SetAging(Core, dependent, 15.7, 0.0, false);
            world.AddFamilyLink(Core, dependent, elder, "Parent");
            world.AddFamilyLink(Core, elder, dependent, "Child");
            world.AddFamilyLink(Core, elder, mover, "Spouse");
            world.AddFamilyLink(Core, mover, elder, "Spouse");
            world.AddFamilyLink(Core, mover, roommate, "Spouse");
            world.AddFamilyLink(Core, roommate, mover, "Spouse");
            world.SetCareRequired(Core, dependent, true);
            for (int i = 0; i < rows.Length; i++)
            {
                ResidentRecord member;
                if (world.TryGetResident(rows[i].Id, out member) && member.Stage >= LifeStage.Teen)
                {
                    club.SetMembership(Core, LiveClub, rows[i].Id, true);
                }
            }
            world.AssignCare(Core, new CareAssignment(dependent, elder,
                new CarePlacement(households[3].Id, -1), default(EventId)));
            housing.SetCaregiver(Core, carrier, true);
            housing.SetCaregiver(Core, other, true);
            housing.SetCaregiver(Core, mover, true);
            housing.SetCaregiver(Core, roommate, true);
            housing.SetCaregiver(Core, elder, true);
            housing.SetCaregiver(Core, exposed, true);

            family.SetReproductiveCapabilities(Core, carrier, true, false);
            family.SetReproductiveCapabilities(Core, other, false, true);
            family.SetPartner(Core, carrier, other, true);

            for (int i = 0; i < rows.Length; i++)
            {
                for (int n = 0; n < StateSchema.NeedCount; n++)
                {
                    world.SetNeed(Core, rows[i].Id, (Need)n, 70f);
                }
                world.SetMoney(Core, rows[i].Id, 0L);
            }
            world.SetNeed(Core, roommate, Need.Hunger, 11f);
            world.SetMoney(Core, elder, 100L);
            // A starting pantry (4 GroceryUnits, 4 PreparedMeals per home) so local
            // autonomy has a Hunger remedy; residents start with no money.
            for (int i = 0; i < homes.Length; i++)
            {
                food.SetHomeStock(Core, homes[i].Id, 4, 4);
            }

            Position(rows[0].Id, 4f, 1.5f);
            Position(rows[1].Id, 5.2f, 1.5f);
            Position(rows[2].Id, 6.4f, 1.5f);
            Position(rows[3].Id, 7.6f, 1.5f);
            Position(rows[7].Id, 8.8f, 1.5f);
            Position(rows[4].Id, 10f, 1.5f);
            Position(rows[5].Id, 11.2f, 1.5f);
            Position(rows[6].Id, 12.4f, 1.5f);

            bedPose = new ApproachPose(4f, 0f, 3.5f);
            bed = world.AllocateObject(Core);
            executor.RegisterObject(bed, new[] { bedPose, bedPose });
            family.RegisterBed(Core, bed, homes[0].Id);

            storm = new SevereStorm(world, executor, home, club);
            commonObjects = new ObjectId[3];
            for (int i = 0; i < commonObjects.Length; i++)
            {
                commonObjects[i] = world.AllocateObject(Core);
                executor.RegisterObject(commonObjects[i], new[] { new ApproachPose(4f + i, 0f, 4f) });
                storm.RegisterObject(Core, commonObjects[i], true, false, true, null);
            }
            roof = world.AllocateObject(Core);
            executor.RegisterObject(roof, new[] { new ApproachPose(-16f, 0f, 2f) });
            storm.RegisterObject(Core, roof, false, false, false, homes[0].Id);
            electric = world.AllocateObject(Core);
            executor.RegisterObject(electric, new[] { new ApproachPose(8f, 0f, 8f) });
            storm.RegisterObject(Core, electric, false, true, false, null);
            outdoor = world.AllocateObject(Core);
            executor.RegisterObject(outdoor, new[] { new ApproachPose(10f, 0f, 10f) });
            storm.RegisterObject(Core, outdoor, true, false, false, null);
            shelter = world.AllocateObject(Core);
            executor.RegisterObject(shelter, new[] { new ApproachPose(8f, 0f, 4f) });
            storm.RegisterObject(Core, shelter, false, false, false, null);
            storm.SetOutdoors(Core, exposed, true, shelter);

            aging = new Aging(world, clock);
            estate = new EstateCare(game);
            scheduler = new PopulationScheduler(world, clock, commands, executor, autonomy);
            scheduler.Offered = Offered;
            context = new TierContext(new ResidentId[0], null, null, null);
            frame = 0;
            settledGameSeconds = world.GameSeconds;
            seenEvents = world.Events.Count;
        }

        // ---------- PARTY-LIVE (M5-OD-50/51/52) ----------

        public const long PartyTravelLeadGameSeconds = 15L * 60L;
        public const long PartyEveningGameSeconds = 19L * 3600L;
        public const int AutonomousGuests = 3;
        public static readonly string[] PartyTimes = { "Now", "Tonight 7 PM", "Tomorrow 7 PM" };

        public sealed class LiveNotice
        {
            public readonly string Text;
            public readonly bool Slowdown;
            public LiveNotice(string text, bool slowdown) { Text = text; Slowdown = slowdown; }
        }

        // The HUD's ping order (M5-OD-52): the newest slowdown's explanation shows at once,
        // ahead of older pings; an informational ping it cuts short (a payoff) shows again after.
        public sealed class PingBoard
        {
            private readonly List<string> queue = new List<string>();
            private int urgent;          // leading queue entries that explain a slowdown
            private int fresh;           // slowdown pings added since the last Next
            private string shown;
            private bool shownUrgent;
            private float until;

            public void Add(LiveNotice notice)
            {
                if (!notice.Slowdown) { queue.Add(notice.Text); return; }
                queue.Insert(fresh++, notice.Text);
                urgent++;
            }

            // The ping to show at real time now, or null to keep the current one.
            public string Next(float now)
            {
                bool preempt = fresh > 0 && now < until;
                fresh = 0;
                if (preempt && shown != null && !shownUrgent) queue.Insert(urgent, shown);
                if (queue.Count == 0 || (!preempt && now < until)) return null;
                shown = queue[0];
                queue.RemoveAt(0);
                shownUrgent = urgent > 0;
                if (shownUrgent) urgent--;
                // Party pings hold longer than need pings so the payoff stays readable.
                until = now + (shown.Contains("party") ? 5f : 3f);
                return shown;
            }
        }

        private readonly List<LiveNotice> notices = new List<LiveNotice>();
        private readonly HashSet<ResidentId> critical = new HashSet<ResidentId>();
        private int seenEvents;

        // Notices since the last drain (party answers, arrivals, payoff, slowdown pings).
        public List<LiveNotice> DrainNotices()
        {
            List<LiveNotice> drained = new List<LiveNotice>(notices);
            notices.Clear();
            return drained;
        }

        // Start time for a PartyTimes choice; -1 when "Tonight" is already past its travel lead.
        public long PartyStartFor(int choice)
        {
            long now = world.GameSeconds;
            long evening = now / Day * Day + PartyEveningGameSeconds;
            if (choice == 0) return now;
            if (choice == 1) return now <= evening - PartyTravelLeadGameSeconds ? evening : -1L;
            if (choice == 2) return evening + Day;
            return -1L;
        }

        // Default invite list: the host's strongest friendships first, then roster order.
        public List<ResidentId> TopBonds(ResidentId host, int count)
        {
            ResidentRecord record;
            List<ResidentId> picks = new List<ResidentId>();
            if (!world.TryGetResident(host, out record)) return picks;
            List<KeyValuePair<float, ResidentId>> ranked = new List<KeyValuePair<float, ResidentId>>();
            foreach (ResidentRecord other in world.ResidentsSnapshot())
            {
                PartyPlan busy = OpenPlanFor(other.Id);
                if (other.Id == host || !other.IsAlive || (busy != null && busy.Host != (long)host)) continue;
                float friendship = 0f;
                foreach (var bond in record.Bonds) if (bond.Other == other.Id) friendship = bond.Friendship;
                ranked.Add(new KeyValuePair<float, ResidentId>(friendship, other.Id));
            }
            ranked.Sort((a, b) => a.Key != b.Key ? b.Key.CompareTo(a.Key) : ((long)a.Value).CompareTo((long)b.Value));
            for (int i = 0; i < ranked.Count && picks.Count < count; i++) picks.Add(ranked[i].Value);
            return picks;
        }

        // Plan a party: each guest answers now with the ordinary acceptance draw (friendship,
        // romance, Sociable and active negative feelings toward the host). Nobody accepting
        // closes the plan at once, so no live party is ever guestless.
        public PartyPlan PlanParty(ResidentId host, long start, IList<ResidentId> guests, long venue, out string summary)
        {
            summary = string.Empty;
            ResidentRecord record;
            if (!world.TryGetResident(host, out record) || !record.IsAlive || record.Stage < LifeStage.Teen)
            {
                summary = "Only a living teen or older resident can host.";
                return null;
            }
            PartyPlan earlier = OpenPlanFor(host);
            if (earlier != null && (earlier.Host != (long)host || earlier.TravelStarted || earlier.HostAction != 0L))
            {
                summary = record.DisplayName + (earlier.Host == (long)host ? "'s party is already under way." : " is going to another party.");
                return null;
            }
            // A new plan replaces the host's own plan that has not started (the player overrides autonomy).
            if (earlier != null) earlier.Closed = true;
            if (guests == null || guests.Count == 0) { summary = "Invite at least one guest."; return null; }
            long now = world.GameSeconds;
            if (start < now) start = now;
            PartyPlan plan = new PartyPlan();
            plan.Id = 1L;
            foreach (PartyPlan other in game.Party.Plans) if (other.Id >= plan.Id) plan.Id = other.Id + 1L;
            plan.Host = (long)host;
            plan.Created = now;
            plan.Start = start;
            plan.Venue = venue;
            List<string> answers = new List<string>();
            int accepted = 0;
            foreach (ResidentId guest in guests)
            {
                ResidentRecord guestRecord;
                if (guest == host || !world.TryGetResident(guest, out guestRecord) || !guestRecord.IsAlive) continue;
                if (plan.Invites.Exists(i => i.Guest == (long)guest)) continue;
                // An answer already given to this host stands; a guest committed to another open
                // party declines without a draw.
                PartyInvite asked = earlier != null ? earlier.Invites.Find(i => i.Guest == (long)guest) : null;
                bool yes = asked != null ? asked.Accepted : OpenPlanFor(guest) == null && autonomy.DrawAcceptance(guest, host);
                plan.Invites.Add(new PartyInvite { Guest = (long)guest, Accepted = yes });
                answers.Add(guestRecord.DisplayName + (yes ? " accepted" : " declined"));
                if (yes) accepted++;
            }
            if (plan.Invites.Count == 0) { summary = "Invite at least one living guest."; return null; }
            plan.Closed = accepted == 0;
            game.Party.Plans.Add(plan);
            summary = string.Join(" · ", answers.ToArray())
                + (accepted == 0 ? " — nobody can come, party called off." : " — " + record.DisplayName + "'s party " + When(start) + ".");
            notices.Add(new LiveNotice(summary, false));
            return plan;
        }

        // An open plan the resident hosts or has accepted.
        public PartyPlan OpenPlanFor(ResidentId resident)
        {
            foreach (PartyPlan plan in game.Party.Plans)
            {
                if (plan.Closed) continue;
                if (plan.Host == (long)resident) return plan;
                if (plan.Invites.Exists(i => i.Accepted && i.Guest == (long)resident)) return plan;
            }
            return null;
        }

        // Game seconds until the resident's hosted party starts; null when none is pending.
        public long? PartyCountdown(ResidentId host)
        {
            PartyPlan plan = game.Party.OpenPlan(host);
            if (plan == null || plan.Announced) return null;
            return Math.Max(0L, plan.Start - world.GameSeconds);
        }

        private string When(long start)
        {
            long now = world.GameSeconds;
            if (start - now < 60L) return "starts now";
            long day = start / Day - now / Day;
            long hour = start % Day / 3600L;
            string clock = ((hour + 11L) % 12L + 1L).ToString() + ":" + (start % 3600L / 60L).ToString("00")
                + (hour < 12L ? " AM" : " PM");
            return "at " + clock + (day == 0L ? " tonight" : day == 1L ? " tomorrow" : " on day " + (start / Day + 1L).ToString());
        }

        // One plan per host per game day, and only with someone free to invite.
        private bool CanPlanParty(ResidentId host)
        {
            long now = world.GameSeconds;
            foreach (PartyPlan plan in game.Party.Plans)
            {
                if (plan.Host == (long)host && (!plan.Closed || now - plan.Created < Day)) return false;
            }
            return OpenPlanFor(host) == null && TopBonds(host, 1).Count > 0;
        }

        // Autonomy chose ACT-PARTY-01: withdraw the bare action and plan the party for the
        // evening instead, inviting the host's top bonds.
        private void PlanAutonomousParties()
        {
            foreach (ResidentRecord record in world.ResidentsSnapshot())
            {
                if (!record.IsAlive) continue;
                ActionEntry current = commands.GetCurrent(record.Id);
                bool currentBare = current != null && current.Owner == ActionOwner.Autonomous
                    && current.Kind == CommandKind.DirectActivity && current.Activity.Code == "ACT-PARTY-01";
                bool bare = currentBare;
                foreach (ActionEntry pending in commands.GetPending(record.Id))
                {
                    if (pending.Owner != ActionOwner.Autonomous || pending.Activity.Code != "ACT-PARTY-01") continue;
                    commands.Submit(CancelQueued(record.Id, pending.Command));
                    bare = true;
                }
                if (!bare) continue;
                if (currentBare) commands.Submit(Cancel(record.Id));
                if (!CanPlanParty(record.Id)) continue;
                long start = PartyStartFor(1);
                if (start < 0L) start = PartyStartFor(2);
                string summary;
                PlanParty(record.Id, start, TopBonds(record.Id, AutonomousGuests), 0L, out summary);
            }
        }

        private void TickParties()
        {
            long now = world.GameSeconds;
            List<PartyPlan> plans = game.Party.Plans;
            // Closed plans stay one game day: they carry the one-plan-per-day autonomy rule.
            plans.RemoveAll(p => p.Closed && now - p.Created >= Day);
            foreach (PartyPlan plan in plans.ToArray())
            {
                if (plan.Closed) continue;
                ResidentId host = (ResidentId)plan.Host;
                ResidentRecord hostRecord;
                if (!world.TryGetResident(host, out hostRecord) || !hostRecord.IsAlive)
                {
                    ClosePlan(plan, false, NameOf(plan.Host) + "'s party was called off.");
                    continue;
                }
                if (!plan.TravelStarted)
                {
                    if (now < plan.Start - PartyTravelLeadGameSeconds) continue;
                    StartPartyTravel(plan, hostRecord);
                }
                // The dated start is the host's Perform/charge/duration boundary; only travel goes early.
                if (plan.HostAction == 0L)
                {
                    if (now < plan.Start) continue;
                    StartHosting(plan, hostRecord);
                    if (plan.Closed) continue;
                }
                ActionInstanceId party = (ActionInstanceId)plan.HostAction;
                if (!plan.Announced && now >= plan.Start)
                {
                    plan.Announced = true;
                    notices.Add(new LiveNotice(hostRecord.DisplayName + "'s party is starting!", true));
                }
                // Early guests wait at the host; attendance begins once the party performs.
                bool hosting = Performing(host, "ACT-PARTY-01");
                foreach (PartyInvite invite in plan.Invites)
                {
                    if (!hosting || !invite.Sent || invite.Arrived || !Performing((ResidentId)invite.Guest, "ACT-PARTY-02")) continue;
                    invite.Arrived = true;
                    game.Party.Enter(Exec, party, (ResidentId)invite.Guest);
                    notices.Add(new LiveNotice(NameOf(invite.Guest) + " arrived at " + hostRecord.DisplayName + "'s party.", true));
                }
                ScheduleChats(plan);
                bool completed;
                if (game.Party.IsDone(party, out completed))
                {
                    ClosePlan(plan, completed, null);
                    continue;
                }
                // A guest who stops attending (player redirect, conflicting command) departs.
                foreach (PartyInvite invite in plan.Invites)
                {
                    if (invite.Arrived && !AtParty((ResidentId)invite.Guest)) game.Party.Exit(Exec, party, (ResidentId)invite.Guest);
                }
            }
        }

        // Guests set off at start minus the travel lead and walk to the host; a host with a
        // venue walks there too, so the party itself can begin on the dated start.
        private void StartPartyTravel(PartyPlan plan, ResidentRecord host)
        {
            plan.TravelStarted = true;
            if (plan.Venue > 0L)
            {
                commands.Submit(new CommandEnvelope(world.AllocateCommand(Core), world.Epoch, world.GameSeconds,
                    CommandSource.PlayerHuman, commands.CurrentStamp(host.Id).Sequence + 1u, CommandKind.TravelTo,
                    host.Id, null, plan.Venue, new CommandParameters.Travel(QueueMode.Immediate, TravelTargetKind.Object)));
            }
            foreach (PartyInvite invite in plan.Invites)
            {
                ResidentRecord guest;
                if (!invite.Accepted || !world.TryGetResident((ResidentId)invite.Guest, out guest) || !guest.IsAlive) continue;
                invite.Sent = commands.Submit(Envelope(guest.Id, "ACT-PARTY-02", plan.Host)).Accepted;
            }
        }

        private void StartHosting(PartyPlan plan, ResidentRecord host)
        {
            CommandResult hosted = commands.Submit(Envelope(host.Id, "ACT-PARTY-01", plan.Venue > 0L ? plan.Venue : (long?)null));
            ActionEntry current = commands.GetCurrent(host.Id);
            if (!hosted.Accepted || current == null || current.Activity.Code != "ACT-PARTY-01")
            {
                ClosePlan(plan, false, host.DisplayName + " couldn't host the party (" + hosted.Reason + ").");
                return;
            }
            plan.HostAction = (long)current.Action;
        }

        // SDoc ACT-PARTY-01: at least one Chat for each attendee when a partner is there.
        // The guest steps out of attendance for the Chat and rejoins right after.
        private void ScheduleChats(PartyPlan plan)
        {
            List<PartyInvite> here = plan.Invites.FindAll(i => i.Arrived && AtParty((ResidentId)i.Guest));
            foreach (PartyInvite invite in here)
            {
                if (invite.Chatted) continue;
                PartyInvite partner = here.Find(o => o != invite && !o.Chatted) ?? here.Find(o => o != invite);
                if (partner == null) continue;
                ResidentId guest = (ResidentId)invite.Guest;
                if (!commands.Submit(Envelope(guest, "ACT-REL-01", partner.Guest)).Accepted) continue;
                commands.Submit(Envelope(guest, "ACT-PARTY-02", plan.Host, QueueMode.Append));
                invite.Chatted = true;
                partner.Chatted = true;
            }
        }

        private void ClosePlan(PartyPlan plan, bool completed, string reason)
        {
            plan.Closed = true;
            ResidentId host = (ResidentId)plan.Host;
            ResidentRecord hostRecord;
            world.TryGetResident(host, out hostRecord);
            string hostName = hostRecord != null ? hostRecord.DisplayName : "The host";
            List<string> came = new List<string>();
            List<string> missing = new List<string>();
            List<string> lonely = new List<string>();
            foreach (PartyInvite invite in plan.Invites)
            {
                if (!invite.Accepted) continue;
                ResidentId guest = (ResidentId)invite.Guest;
                if (invite.Sent)
                {
                    ActionEntry going = commands.GetCurrent(guest);
                    if (going != null && going.Activity.Code == "ACT-PARTY-02") commands.Submit(Cancel(guest));
                    foreach (ActionEntry pending in commands.GetPending(guest))
                    {
                        if (pending.Activity.Code == "ACT-PARTY-02") commands.Submit(CancelQueued(guest, pending.Command));
                    }
                }
                if (invite.Arrived)
                {
                    came.Add(NameOf(invite.Guest));
                    if (!invite.Chatted) lonely.Add(NameOf(invite.Guest));
                }
                else if (plan.HostAction != 0L && hostRecord != null)
                {
                    invite.NoShow = true;
                    missing.Add(NameOf(invite.Guest));
                    NoShow(hostRecord, guest);
                }
            }
            string text = reason;
            if (text == null)
            {
                text = completed
                    ? hostName + "'s party was a hit! " + (came.Count > 0 ? string.Join(", ", came.ToArray()) + " had a great time (+Social +Fun)." : "")
                    : hostName + "'s party ended early.";
                if (completed && HasPartyFeeling(host, FeelingKind.Proud)) text += " " + hostName + " feels Proud.";
                if (missing.Count > 0) text += " " + string.Join(", ", missing.ToArray()) + " didn't show.";
                // SDoc: record why a guest had no party Chat.
                if (lonely.Count > 0) text += " " + string.Join(", ", lonely.ToArray()) + " had nobody else to chat with.";
            }
            notices.Add(new LiveNotice(text, false));
        }

        // A no-show breaks the commitment: the host is Hurt (intensity 1, one game day) and
        // both remember it, written through the ordinary feeling and memory records.
        private void NoShow(ResidentRecord host, ResidentId guest)
        {
            long now = world.GameSeconds;
            WorldEvent missed = world.AppendEvent(Exec, "party-no-show", guest, "ACT-PARTY-01 host=" + ((long)host.Id).ToString());
            world.AddFeeling(Exec, host.Id, FeelingKind.Hurt, missed.Event, guest, 1, now + Day);
            List<ResidentId> who = new List<ResidentId> { host.Id, guest };
            world.AddMemory(Exec, host.Id, new MemoryEntry(missed.Event, missed.Kind, missed.GameSeconds, host.Home, who, "no-show", "participant"));
            ResidentRecord guestRecord;
            if (world.TryGetResident(guest, out guestRecord))
            {
                world.AddMemory(Exec, guest, new MemoryEntry(missed.Event, missed.Kind, missed.GameSeconds, guestRecord.Home, who, "no-show", "participant"));
            }
        }

        private bool HasPartyFeeling(ResidentId resident, FeelingKind kind)
        {
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record)) return false;
            foreach (Feeling feeling in record.Feelings)
            {
                if (feeling.Kind == kind && feeling.ExpiryGameSeconds > world.GameSeconds) return true;
            }
            return false;
        }

        // M5-OD-52: an argument or a need crossing the critical line drops the speed to Normal with a ping;
        // party starts and arrivals ask for the same through their notices.
        private void WatchSlowdownEvents()
        {
            IReadOnlyList<WorldEvent> events = world.Events;
            for (; seenEvents < events.Count; seenEvents++)
            {
                WorldEvent entry = events[seenEvents];
                if (entry.Kind == "argument" && entry.Actor.HasValue)
                {
                    notices.Add(new LiveNotice(NameOf((long)entry.Actor.Value) + " started an argument.", true));
                }
                else if (entry.Kind == "Death" && entry.Actor.HasValue)
                {
                    ResidentId deceased = entry.Actor.Value;
                    string deceasedName = NameOf((long)deceased);
                    ResidentId? careDependent = null;
                    ResidentId? careGuardian = null;
                    for (int i = 0; i < world.CareAssignments.Count; i++)
                    {
                        if (world.CareAssignments[i].Guardian == deceased)
                        {
                            ResidentId dep = world.CareAssignments[i].Dependent;
                            for (int j = world.CareAssignments.Count - 1; j >= 0; j--)
                            {
                                if (world.CareAssignments[j].Dependent == dep && world.CareAssignments[j].Guardian != deceased)
                                {
                                    careDependent = dep;
                                    careGuardian = world.CareAssignments[j].Guardian;
                                    break;
                                }
                            }
                            if (careDependent.HasValue) break;
                        }
                    }
                    string text = careDependent.HasValue && careGuardian.HasValue
                        ? deceasedName + " has passed away. " + NameOf((long)careDependent.Value) + " is now cared for by " + NameOf((long)careGuardian.Value) + "."
                        : deceasedName + " has passed away.";
                    notices.Add(new LiveNotice(text, true));
                }
            }
            foreach (ResidentRecord record in world.ResidentsSnapshot())
            {
                if (!record.IsAlive) { critical.Remove(record.Id); continue; }
                Need? low = null;
                for (int n = 0; n < StateSchema.NeedCount && !low.HasValue; n++)
                {
                    if (record.GetNeed((Need)n) <= CriticalNeed) low = (Need)n;
                }
                if (!low.HasValue) { critical.Remove(record.Id); continue; }
                if (critical.Add(record.Id))
                {
                    notices.Add(new LiveNotice(record.DisplayName + "'s " + low.Value + " is critical!", true));
                }
            }
            foreach (LiveNotice notice in notices)
            {
                if (notice.Slowdown && clock.Speed != SimulationSpeed.Normal)
                {
                    clock.SetSpeed(SimulationSpeed.Normal);
                    break;
                }
            }
        }

        private bool Performing(ResidentId resident, string code)
        {
            ActionEntry current = commands.GetCurrent(resident);
            if (current == null || current.Activity.Code != code) return false;
            ExecutionRecord record = executor.GetExecution(current.Action);
            return record != null && !record.IsTerminal && record.Stage == ExecutionStage.Perform;
        }

        // Still attending: an attendance or the scheduled Chat runs or waits in the queue.
        private bool AtParty(ResidentId guest)
        {
            ActionEntry current = commands.GetCurrent(guest);
            if (current != null && (current.Activity.Code == "ACT-PARTY-02" || current.Activity.Code == "ACT-REL-01")) return true;
            foreach (ActionEntry pending in commands.GetPending(guest))
            {
                if (pending.Activity.Code == "ACT-PARTY-02") return true;
            }
            return false;
        }

        private string NameOf(long resident)
        {
            ResidentRecord record;
            return world.TryGetResident((ResidentId)resident, out record) ? record.DisplayName : "A guest";
        }

        private CommandEnvelope Cancel(ResidentId resident)
        {
            return new CommandEnvelope(world.AllocateCommand(Core), world.Epoch, world.GameSeconds,
                CommandSource.PlayerHuman, commands.CurrentStamp(resident).Sequence + 1u,
                CommandKind.CancelCurrent, resident, null, null, CommandParameters.None.Instance);
        }

        private CommandEnvelope CancelQueued(ResidentId resident, CommandId command)
        {
            return new CommandEnvelope(world.AllocateCommand(Core), world.Epoch, world.GameSeconds,
                CommandSource.PlayerHuman, commands.CurrentStamp(resident).Sequence + 1u,
                CommandKind.CancelQueued, resident, null, (long)command, CommandParameters.None.Instance);
        }

        private void Position(ResidentId resident, float x, float z)
        {
            executor.SetResidentPosition(resident, new ApproachPose(x, 0f, z));
        }

        private bool BedFor(HomeId target, out ObjectId authoredBed)
        {
            authoredBed = default(ObjectId);
            FamilyCreation.FamilySnapshot snapshot = family.Export();
            for (int i = 0; i < snapshot.Beds.Count; i++)
            {
                if ((long)snapshot.Beds[i].Home == (long)target)
                {
                    authoredBed = (ObjectId)snapshot.Beds[i].Object;
                    return true;
                }
            }
            return false;
        }

        private CommandEnvelope Envelope(ResidentId resident, string code, long? target,
            QueueMode mode = QueueMode.Immediate)
        {
            return new CommandEnvelope(world.AllocateCommand(Core), world.Epoch,
                world.GameSeconds, CommandSource.PlayerHuman,
                commands.CurrentStamp(resident).Sequence + 1u,
                CommandKind.DirectActivity, resident, new ActivityId(code), target,
                new CommandParameters.Direct(mode));
        }

        private static int CompareResidents(ResidentRecord a, ResidentRecord b)
        {
            return ((long)a.Id).CompareTo((long)b.Id);
        }

        private static Tuning ReadTuning(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Missing live tuning", path);
            }
            JsonValue parsed = JsonParser.Parse(File.ReadAllText(path));
            JsonObject root = parsed as JsonObject;
            if (root == null || !root.Has("live_play"))
            {
                throw new ArgumentException("tuning.json has no live_play section", "path");
            }
            JsonObject live = root.GetObject("live_play", path);
            string speedText = live.GetString("speed", path);
            string presetText = live.GetString("aging_preset", path);
            double days = live.GetDouble("initial_world_age_days", path);
            int bound = live.GetInt("transition_bound_days", path);
            SimulationSpeed speed;
            if (!Enum.TryParse(speedText, out speed) || speed == SimulationSpeed.Paused)
            {
                throw new ArgumentException("live_play.speed is not a running speed", "path");
            }
            AgingPreset preset;
            if (!Enum.TryParse(presetText, out preset) || preset == AgingPreset.Off)
            {
                throw new ArgumentException("live_play.aging_preset must advance aging", "path");
            }
            if (days < 0.0 || days > 30.0 || bound <= 0)
            {
                throw new ArgumentException("live_play replay bounds are outside the contract", "path");
            }
            Tuning tuning = new Tuning();
            tuning.Speed = speed;
            tuning.Preset = preset;
            tuning.InitialGameSeconds = (long)Math.Round(days * (double)Day);
            return tuning;
        }
    }
}
