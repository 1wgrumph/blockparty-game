// PERSONALITY production checks. Standalone console program: compiled and run
// with the Unity-bundled Roslyn + netcorerun until TOOLCHAIN lands the Unity
// test path. Positive checks prove BP-SEC-05 scoring (needs, trait/social/
// history/routine factors, bounded seeded jitter), the remedy restriction,
// routine windows, recorded social draws and complete decision logs whose
// scores an independent oracle recomputes within 1e-6. Negative checks prove
// unknown codes, player-only work, bad targets and out-of-range jitter never
// reach scoring, and autonomy yields to player queues, busy and queued
// residents. Mirrors the NAV/COMMAND/CLOCK/STATE pattern.
using System;
using System.Collections.Generic;
using System.Reflection;
using Blockparty.Contracts;
using Blockparty.Simulation;

public static class PersonalityChecks
{
    private const StateWriter Core = StateWriter.SimCore;

    private static int failures;
    private static long nextId = 5000;

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
    }

    private static Stack NewStack(long seed)
    {
        WorldState world = WorldState.CreateSeeded(7L, seed, 0, null);
        Clock clock = new Clock(world);
        CommandArbitrator arbitrator = new CommandArbitrator(world, clock);
        return new Stack
        {
            World = world,
            Clock = clock,
            Arbitrator = arbitrator,
            Autonomy = new Autonomy(world, arbitrator),
        };
    }

    private static ResidentRecord AddResident(WorldState world, string name, LifeStage stage)
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
        return world.AllocateResident(Core, name, stage, household, home);
    }

    private static void SetAllNeeds(WorldState world, ResidentRecord resident, float value)
    {
        for (int i = 0; i < StateSchema.NeedCount; i++)
        {
            world.SetNeed(Core, resident.Id, (Need)i, value);
        }
    }

    private static void SetAllTraits(WorldState world, ResidentRecord resident, int value)
    {
        for (int i = 0; i < StateSchema.TraitCount; i++)
        {
            world.SetTrait(Core, resident.Id, (TraitAxis)i, value);
        }
    }

    private static CommandResult Direct(Stack stack, ResidentRecord resident, string activity,
        QueueMode mode, long? targetId)
    {
        ResidentRecord current;
        stack.World.TryGetResident(resident.Id, out current);
        nextId++;
        return stack.Arbitrator.Submit(new CommandEnvelope(
            (CommandId)nextId, stack.World.Epoch, stack.World.GameSeconds,
            CommandSource.PlayerHuman, current.ResidentSequence + 1u,
            CommandKind.DirectActivity, resident.Id, new ActivityId(activity),
            targetId, new CommandParameters.Direct(mode)));
    }

    private static CandidateScore FindCandidate(AutonomyDecision decision, string code)
    {
        for (int i = 0; i < decision.Candidates.Count; i++)
        {
            if (string.Equals(decision.Candidates[i].Activity.Code, code, StringComparison.Ordinal))
            {
                return decision.Candidates[i];
            }
        }
        return null;
    }

    private static int RankOf(AutonomyDecision decision, string code)
    {
        List<CandidateScore> ordered = new List<CandidateScore>();
        for (int i = 0; i < decision.Candidates.Count; i++)
        {
            ordered.Add(decision.Candidates[i]);
        }
        ordered.Sort(delegate(CandidateScore a, CandidateScore b)
        {
            if (a.Score != b.Score)
            {
                return b.Score.CompareTo(a.Score);
            }
            int codeOrder = string.Compare(a.Activity.Code, b.Activity.Code, StringComparison.Ordinal);
            if (codeOrder != 0)
            {
                return codeOrder;
            }
            long at = a.TargetId.HasValue ? a.TargetId.Value : -1L;
            long bt = b.TargetId.HasValue ? b.TargetId.Value : -1L;
            return at.CompareTo(bt);
        });
        for (int i = 0; i < ordered.Count; i++)
        {
            if (string.Equals(ordered[i].Activity.Code, code, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    // Independent oracle: recompute one score from logged inputs only.
    private static double OracleScore(CandidateScore candidate)
    {
        return candidate.Benefit * candidate.TraitFactor * candidate.SocialFactor
            * candidate.HistoryFactor * candidate.RoutineFactor * candidate.Jitter
            / (1.0 + candidate.PathMinutes / 60.0);
    }

    private static bool OracleMatches(AutonomyDecision decision)
    {
        CandidateScore winner = null;
        for (int i = 0; i < decision.Candidates.Count; i++)
        {
            CandidateScore candidate = decision.Candidates[i];
            if (Math.Abs(OracleScore(candidate) - candidate.Score) > 1e-6)
            {
                return false;
            }
            if (winner == null || OracleBeats(candidate, winner))
            {
                winner = candidate;
            }
        }
        return winner != null && winner.Activity == decision.Chosen
            && winner.TargetId == decision.ChosenTarget;
    }

    private static bool OracleBeats(CandidateScore challenger, CandidateScore holder)
    {
        if (challenger.Score != holder.Score)
        {
            return challenger.Score > holder.Score;
        }
        int code = string.Compare(challenger.Activity.Code, holder.Activity.Code, StringComparison.Ordinal);
        if (code != 0)
        {
            return code < 0;
        }
        long ct = challenger.TargetId.HasValue ? challenger.TargetId.Value : -1L;
        long ht = holder.TargetId.HasValue ? holder.TargetId.Value : -1L;
        return ct < ht;
    }

    private static bool JitterInRange(AutonomyDecision decision)
    {
        for (int i = 0; i < decision.Candidates.Count; i++)
        {
            if (decision.Candidates[i].Jitter < 0.9 || decision.Candidates[i].Jitter > 1.1)
            {
                return false;
            }
        }
        return true;
    }

    public static int Main()
    {
        // 1. Hungry resident remedies Hunger: Hunger 8 restricts scoring to
        // Hunger remedies, picks one, enqueues it as Autonomous, logs all.
        {
            Stack stack = NewStack(42L);
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            SetAllNeeds(stack.World, r1, 80.0f);
            SetAllTraits(stack.World, r1, 0);
            stack.World.SetNeed(Core, r1.Id, Need.Hunger, 8.0f);
            AutonomyDecision decision = stack.Autonomy.Decide(r1.Id, 1.0);
            bool onlyRemedies = decision.Candidates.Count > 0;
            for (int i = 0; i < decision.Candidates.Count; i++)
            {
                string code = decision.Candidates[i].Activity.Code;
                if (!string.Equals(code, "ACT-FOOD-01", StringComparison.Ordinal)
                    && !string.Equals(code, "ACT-FOOD-02", StringComparison.Ordinal)
                    && !string.Equals(code, "ACT-FOOD-04", StringComparison.Ordinal))
                {
                    onlyRemedies = false;
                }
            }
            ActionEntry[] pending = stack.Arbitrator.GetPending(r1.Id);
            Check(decision.HasChoice && decision.Trigger == "Remedy:Hunger"
                && decision.BlockedReason.Length == 0 && onlyRemedies
                && decision.CommittedEffect == "Enqueued:" + decision.Chosen.Code
                && pending.Length == 1 && pending[0].Owner == ActionOwner.Autonomous
                && pending[0].Activity == decision.Chosen
                && OracleMatches(decision), "hunger-remedy-restricted-enqueued");
        }

        // 2. Oracle recompute over a full snapshot decision with fixed jitter.
        {
            Stack stack = NewStack(42L);
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            SetAllNeeds(stack.World, r1, 80.0f);
            stack.World.SetNeed(Core, r1.Id, Need.Hunger, 40.0f);
            stack.World.SetNeed(Core, r1.Id, Need.Energy, 60.0f);
            stack.World.SetNeed(Core, r1.Id, Need.Fun, 20.0f);
            SetAllTraits(stack.World, r1, 0);
            stack.World.SetTrait(Core, r1.Id, TraitAxis.Sociable, 20);
            stack.World.SetTrait(Core, r1.Id, TraitAxis.Playful, 10);
            AutonomyDecision decision = stack.Autonomy.Decide(r1.Id, 1.0);
            Check(decision.HasChoice && decision.Candidates.Count > 10
                && OracleMatches(decision) && JitterInRange(decision),
                "oracle-recomputes-full-decision");
        }

        // 3. BP-CHR-03 activity preference: paired worlds, same seed, only
        // Sociable differs (-50 vs +50); at least one rank changes.
        {
            AutonomyDecision low = PairedDecision(42L, TraitAxis.Sociable, -50);
            AutonomyDecision high = PairedDecision(42L, TraitAxis.Sociable, 50);
            bool rankChanged = false;
            for (int i = 0; i < low.Candidates.Count; i++)
            {
                if (RankOf(low, low.Candidates[i].Activity.Code)
                    != RankOf(high, low.Candidates[i].Activity.Code))
                {
                    rankChanged = true;
                }
            }
            Check(low.HasChoice && high.HasChoice
                && low.Candidates.Count == high.Candidates.Count
                && rankChanged, "preset-change-moves-ranking");
        }

        // 4. BP-CHR-03 social response: acceptance probability moves with the
        // recipient preset; draws are recorded and seeded-reproducible.
        {
            double low = Autonomy.AcceptanceProbability(30.0f, 0.0f, -50, 0);
            double high = Autonomy.AcceptanceProbability(30.0f, 0.0f, 50, 0);
            Stack a = NewStack(42L);
            ResidentRecord a1 = AddResident(a.World, "A1", LifeStage.Adult);
            ResidentRecord a2 = AddResident(a.World, "A2", LifeStage.Adult);
            Stack b = NewStack(42L);
            ResidentRecord b1 = AddResident(b.World, "B1", LifeStage.Adult);
            ResidentRecord b2 = AddResident(b.World, "B2", LifeStage.Adult);
            bool aa = a.Autonomy.DrawAcceptance(a1.Id, a2.Id);
            bool ba = b.Autonomy.DrawAcceptance(b1.Id, b2.Id);
            Check(high > low && low >= 0.05 && high <= 0.95
                && a.Autonomy.DrawLog.Count == 1 && aa == ba
                && a.Autonomy.DrawLog[0].Draw == b.Autonomy.DrawLog[0].Draw
                && a.Autonomy.DrawLog[0].Probability == b.Autonomy.DrawLog[0].Probability,
                "acceptance-moves-with-preset-draws-seeded");
        }

        // 5. Routine windows: Saturday 10:15 doubles ClubMeeting; Monday 08:15
        // doubles school; Friday 09:15 doubles work for employed residents.
        {
            Stack stack = NewStack(42L);
            ResidentRecord teen = AddResident(stack.World, "Teen", LifeStage.Teen);
            stack.World.SetGameSeconds(Core, 5L * 86400L + 36900L);
            AutonomyDecision club = stack.Autonomy.Decide(teen.Id, 1.0);
            CandidateScore clubScore = FindCandidate(club, "ACT-CLUB-01");
            Check(clubScore != null && clubScore.RoutineFactor == 2.0, "club-window-doubles");
        }
        {
            Stack stack = NewStack(42L);
            ResidentRecord teen = AddResident(stack.World, "Teen", LifeStage.Teen);
            stack.World.SetGameSeconds(Core, 29700L);
            AutonomyDecision school = stack.Autonomy.Decide(teen.Id, 1.0);
            CandidateScore schoolScore = FindCandidate(school, "ACT-EDU-01");
            Check(schoolScore != null && schoolScore.RoutineFactor == 2.0, "school-window-doubles");
        }
        {
            Stack stack = NewStack(42L);
            ResidentRecord adult = AddResident(stack.World, "Adult", LifeStage.Adult);
            stack.World.SetCareer(Core, adult.Id, "Barista", 1, 0);
            stack.World.SetGameSeconds(Core, 4L * 86400L + 33300L);
            AutonomyDecision work = stack.Autonomy.Decide(adult.Id, 1.0);
            CandidateScore workScore = FindCandidate(work, "ACT-WORK-01");
            Check(workScore != null && workScore.RoutineFactor == 2.0, "work-window-doubles-employed");
        }
        {
            Stack stack = NewStack(42L);
            ResidentRecord adult = AddResident(stack.World, "Adult", LifeStage.Adult);
            stack.World.SetGameSeconds(Core, 4L * 86400L + 33300L);
            AutonomyDecision work = stack.Autonomy.Decide(adult.Id, 1.0);
            CandidateScore workScore = FindCandidate(work, "ACT-WORK-01");
            Check(workScore != null && workScore.RoutineFactor == 1.0, "work-window-skips-unemployed");
        }

        // 6. Invitation routine factor moves with Sociable: +50 yields more
        // readily than -50 (BP-CHR-03 routine flexibility).
        {
            Stack low = NewStack(42L);
            ResidentRecord rl = AddResident(low.World, "RL", LifeStage.Adult);
            SetAllNeeds(low.World, rl, 80.0f);
            SetAllTraits(low.World, rl, 0);
            low.World.SetTrait(Core, rl.Id, TraitAxis.Sociable, -50);
            Stack high = NewStack(42L);
            ResidentRecord rh = AddResident(high.World, "RH", LifeStage.Adult);
            SetAllNeeds(high.World, rh, 80.0f);
            SetAllTraits(high.World, rh, 0);
            high.World.SetTrait(Core, rh.Id, TraitAxis.Sociable, 50);
            CandidateScore li = FindCandidate(low.Autonomy.Decide(rl.Id, 1.0), "ACT-PARTY-02");
            CandidateScore hi = FindCandidate(high.Autonomy.Decide(rh.Id, 1.0), "ACT-PARTY-02");
            Check(li != null && hi != null && li.RoutineFactor == 0.5
                && hi.RoutineFactor == 1.5, "invitation-flexes-with-sociable");
        }

        // 7. Actual history: latest known help widens to 1.2, argument narrows
        // to 0.8, the event id is logged, unobserved history is never read.
        {
            Stack stack = NewStack(42L);
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            ResidentRecord r2 = AddResident(stack.World, "R2", LifeStage.Adult);
            SetAllNeeds(stack.World, r1, 80.0f);
            SetAllTraits(stack.World, r1, 0);
            WorldEvent help = stack.World.AppendEvent(Core, "help", r2.Id, "gift");
            stack.World.AddMemory(Core, r1.Id, new MemoryEntry(help.Event, "help",
                stack.World.GameSeconds, r1.Home,
                new List<ResidentId> { r1.Id, r2.Id }.AsReadOnly(), "helped", "participant"));
            WorldEvent quarrel = stack.World.AppendEvent(Core, "argument", r2.Id, "fight");
            stack.World.AddMemory(Core, r2.Id, new MemoryEntry(quarrel.Event, "argument",
                stack.World.GameSeconds, r2.Home,
                new List<ResidentId> { r2.Id, r1.Id }.AsReadOnly(), "fought", "participant"));
            AutonomyDecision decision = stack.Autonomy.Decide(r1.Id,
                new List<AvailableAction>
                {
                    new AvailableAction(new ActivityId("ACT-REL-01"), (long)r2.Id),
                }.AsReadOnly(), 1.0);
            CandidateScore chat = FindCandidate(decision, "ACT-REL-01");
            Check(decision.HasChoice && chat != null && chat.HistoryFactor == 1.2
                && chat.HistoryEventId == (long)help.Event, "help-memory-widens-logs-event");
        }
        {
            Stack stack = NewStack(42L);
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            ResidentRecord r2 = AddResident(stack.World, "R2", LifeStage.Adult);
            SetAllNeeds(stack.World, r1, 80.0f);
            SetAllTraits(stack.World, r1, 0);
            WorldEvent quarrel = stack.World.AppendEvent(Core, "argument", r2.Id, "fight");
            stack.World.AddMemory(Core, r1.Id, new MemoryEntry(quarrel.Event, "argument",
                stack.World.GameSeconds, r1.Home,
                new List<ResidentId> { r1.Id, r2.Id }.AsReadOnly(), "fought", "participant"));
            AutonomyDecision decision = stack.Autonomy.Decide(r1.Id,
                new List<AvailableAction>
                {
                    new AvailableAction(new ActivityId("ACT-REL-01"), (long)r2.Id),
                }.AsReadOnly(), 1.0);
            CandidateScore chat = FindCandidate(decision, "ACT-REL-01");
            Check(decision.HasChoice && chat != null && chat.HistoryFactor == 0.8
                && chat.HistoryEventId == (long)quarrel.Event, "argument-memory-narrows");
        }

        // 8. Social factor: bonds and active feelings multiply toward the
        // partner; expired feelings are ignored; the floor holds at 0.1.
        {
            Stack stack = NewStack(42L);
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            ResidentRecord r2 = AddResident(stack.World, "R2", LifeStage.Adult);
            SetAllNeeds(stack.World, r1, 80.0f);
            SetAllTraits(stack.World, r1, 0);
            stack.World.SetBond(Core, r1.Id, r2.Id, 40.0f, 0.0f);
            WorldEvent party = stack.World.AppendEvent(Core, "party", r2.Id, "fun");
            stack.World.AddFeeling(Core, r1.Id, FeelingKind.Grateful, party.Event,
                r2.Id, 2, stack.World.GameSeconds + 86400L);
            stack.World.AddFeeling(Core, r1.Id, FeelingKind.Angry, party.Event,
                r2.Id, 3, 0L);
            AutonomyDecision decision = stack.Autonomy.Decide(r1.Id,
                new List<AvailableAction>
                {
                    new AvailableAction(new ActivityId("ACT-REL-01"), (long)r2.Id),
                }.AsReadOnly(), 1.0);
            CandidateScore chat = FindCandidate(decision, "ACT-REL-01");
            double expected = (1.0 + 40.0 / 200.0) * (1.0 + 0.25 * 2.0);
            Check(chat != null && Math.Abs(chat.SocialFactor - expected) < 1e-9,
                "bond-and-active-feeling-multiply");
        }
        {
            Stack stack = NewStack(42L);
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            ResidentRecord r2 = AddResident(stack.World, "R2", LifeStage.Adult);
            SetAllNeeds(stack.World, r1, 80.0f);
            SetAllTraits(stack.World, r1, 0);
            stack.World.SetBond(Core, r1.Id, r2.Id, -100.0f, -100.0f);
            AutonomyDecision decision = stack.Autonomy.Decide(r1.Id,
                new List<AvailableAction>
                {
                    new AvailableAction(new ActivityId("ACT-REL-01"), (long)r2.Id),
                }.AsReadOnly(), 1.0);
            CandidateScore chat = FindCandidate(decision, "ACT-REL-01");
            double expected = 1.0 - 100.0 / 200.0 - 100.0 / 400.0;
            Check(chat != null && Math.Abs(chat.SocialFactor - expected) < 1e-9
                && chat.SocialFactor >= 0.1,
                "social-factor-floors-at-point-one");
        }

        // 9. Remedy restriction with no remedy: blocked reason recorded, the
        // remaining valid options scored instead of idling.
        {
            Stack stack = NewStack(42L);
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            SetAllNeeds(stack.World, r1, 80.0f);
            SetAllTraits(stack.World, r1, 0);
            stack.World.SetNeed(Core, r1.Id, Need.Hunger, 8.0f);
            AutonomyDecision decision = stack.Autonomy.Decide(r1.Id,
                new List<AvailableAction>
                {
                    new AvailableAction(new ActivityId("ACT-CREA-02"), null),
                    new AvailableAction(new ActivityId("ACT-HEALTH-02"), null),
                }.AsReadOnly(), 1.0);
            Check(decision.HasChoice && decision.Candidates.Count == 2
                && decision.BlockedReason == "NoRemedyFor:Hunger"
                && OracleMatches(decision), "no-remedy-scores-remaining");
        }

        // 10. Yield rules: player queue head, busy current and already-queued
        // residents get no new decision and no new entry.
        {
            Stack stack = NewStack(42L);
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            Direct(stack, r1, "ACT-FOOD-02", QueueMode.Append, null);
            int before = stack.Arbitrator.GetPending(r1.Id).Length;
            AutonomyDecision decision = stack.Autonomy.Decide(r1.Id, 1.0);
            Check(!decision.HasChoice && decision.IdleReason == "PlayerQueued"
                && stack.Arbitrator.GetPending(r1.Id).Length == before,
                "yields-to-player-queue");
        }
        {
            Stack stack = NewStack(42L);
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            Direct(stack, r1, "ACT-FOOD-02", QueueMode.Immediate, null);
            AutonomyDecision decision = stack.Autonomy.Decide(r1.Id, 1.0);
            Check(!decision.HasChoice && decision.IdleReason == "Busy",
                "yields-while-busy");
        }
        {
            Stack stack = NewStack(42L);
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            SetAllNeeds(stack.World, r1, 80.0f);
            SetAllTraits(stack.World, r1, 0);
            stack.Autonomy.Decide(r1.Id, 1.0);
            AutonomyDecision second = stack.Autonomy.Decide(r1.Id, 1.0);
            Check(!second.HasChoice && second.IdleReason == "AlreadyQueued"
                && stack.Arbitrator.GetPending(r1.Id).Length == 1,
                "one-autonomous-entry-at-a-time");
        }

        // 11. Idle with a reason: empty offers idle; the deceased never decide.
        {
            Stack stack = NewStack(42L);
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            AutonomyDecision decision = stack.Autonomy.Decide(r1.Id,
                new List<AvailableAction>().AsReadOnly(), 1.0);
            Check(!decision.HasChoice && decision.IdleReason == "NoValidOption",
                "empty-offers-idle-with-reason");
        }
        {
            Stack stack = NewStack(42L);
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            stack.World.SetAlive(Core, r1.Id, false);
            AutonomyDecision decision = stack.Autonomy.Decide(r1.Id, 1.0);
            Check(!decision.HasChoice && decision.IdleReason == "Deceased",
                "deceased-never-decides");
        }

        // 12. Exact ties break by ActivityId: two gain-free Ambitious options
        // with pinned jitter score identically and the lower code wins.
        {
            Stack stack = NewStack(42L);
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            SetAllNeeds(stack.World, r1, 80.0f);
            SetAllTraits(stack.World, r1, 0);
            AutonomyDecision decision = stack.Autonomy.Decide(r1.Id,
                new List<AvailableAction>
                {
                    new AvailableAction(new ActivityId("ACT-WORK-03"), null),
                    new AvailableAction(new ActivityId("ACT-EDU-03"), null),
                }.AsReadOnly(), 1.0);
            CandidateScore work = FindCandidate(decision, "ACT-WORK-03");
            CandidateScore edu = FindCandidate(decision, "ACT-EDU-03");
            Check(decision.HasChoice && work != null && edu != null
                && work.Score == edu.Score
                && decision.Chosen.Code == "ACT-EDU-03", "ties-break-by-activity-id");
        }

        // 13. Bounded seeded chance: 200 draws stay in [0.9,1.1]; two worlds
        // on one seed draw the identical jitter sequence.
        {
            Stack a = NewStack(99L);
            ResidentRecord a1 = AddResident(a.World, "A1", LifeStage.Adult);
            SetAllNeeds(a.World, a1, 80.0f);
            SetAllTraits(a.World, a1, 0);
            Stack b = NewStack(99L);
            ResidentRecord b1 = AddResident(b.World, "B1", LifeStage.Adult);
            SetAllNeeds(b.World, b1, 80.0f);
            SetAllTraits(b.World, b1, 0);
            AutonomyDecision da = a.Autonomy.Decide(a1.Id);
            AutonomyDecision db = b.Autonomy.Decide(b1.Id);
            bool same = da.Candidates.Count == db.Candidates.Count && da.Candidates.Count > 0;
            for (int i = 0; same && i < da.Candidates.Count; i++)
            {
                same = da.Candidates[i].Jitter == db.Candidates[i].Jitter;
            }
            bool bounded = JitterInRange(da);
            for (int s = 0; s < 6 && bounded; s++)
            {
                Stack extra = NewStack(1000L + s);
                ResidentRecord r = AddResident(extra.World, "R", LifeStage.Adult);
                SetAllNeeds(extra.World, r, 80.0f);
                SetAllTraits(extra.World, r, 0);
                bounded = JitterInRange(extra.Autonomy.Decide(r.Id));
            }
            Check(same && bounded, "jitter-bounded-and-seeded");
        }

        // 14. Not-first-candidate: 20 seeded decisions never blindly take the
        // first-ordered candidate when the oracle argmax differs.
        {
            bool blind = false;
            int ran = 0;
            for (int s = 0; s < 20; s++)
            {
                Stack stack = NewStack(7000L + s);
                ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
                SetAllNeeds(stack.World, r1, 80.0f);
                SetAllTraits(stack.World, r1, 0);
                stack.World.SetTrait(Core, r1.Id, TraitAxis.Sociable, (s % 3 - 1) * 40);
                stack.World.SetNeed(Core, r1.Id, (Need)(s % 8), 15.0f + (float)s);
                AutonomyDecision decision = stack.Autonomy.Decide(r1.Id);
                if (!decision.HasChoice || decision.Candidates.Count == 0)
                {
                    continue;
                }
                ran++;
                if (!OracleMatches(decision))
                {
                    blind = true;
                }
                string first = decision.Candidates[0].Activity.Code;
                if (decision.Chosen.Code == first)
                {
                    bool firstIsBest = true;
                    for (int i = 1; i < decision.Candidates.Count; i++)
                    {
                        if (OracleBeats(decision.Candidates[i], decision.Candidates[0]))
                        {
                            firstIsBest = false;
                        }
                    }
                    if (!firstIsBest)
                    {
                        blind = true;
                    }
                }
            }
            Check(ran == 20 && !blind, "twenty-decisions-follow-argmax");
        }

        // 15. Invalid offers never score: unknown codes, the player-only
        // activity and unknown targets are logged with reasons.
        {
            Stack stack = NewStack(42L);
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            SetAllNeeds(stack.World, r1, 80.0f);
            SetAllTraits(stack.World, r1, 0);
            AutonomyDecision decision = stack.Autonomy.Decide(r1.Id,
                new List<AvailableAction>
                {
                    new AvailableAction(new ActivityId("ACT-NOPE-00"), null),
                    new AvailableAction(new ActivityId("ACT-HOME-04"), null),
                    new AvailableAction(new ActivityId("ACT-REL-01"), 999999L),
                    new AvailableAction(new ActivityId("ACT-CREA-02"), null),
                }.AsReadOnly(), 1.0);
            bool reasons = decision.Invalid.Count == 3;
            bool scored = decision.Candidates.Count == 1
                && decision.Candidates[0].Activity.Code == "ACT-CREA-02";
            Check(decision.HasChoice && reasons && scored, "invalid-offers-logged-not-scored");
        }

        // 16. Boundary inputs throw: unknown residents, null arguments and
        // out-of-range pinned jitter.
        {
            Stack stack = NewStack(42L);
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            bool unknownResident = false;
            bool nullWorld = false;
            bool nullArbitrator = false;
            bool badJitterLow = false;
            bool badJitterHigh = false;
            bool unknownAcceptance = false;
            bool unknownSentiment = false;
            try
            {
                stack.Autonomy.Decide((ResidentId)424242L, 1.0);
            }
            catch (ArgumentException)
            {
                unknownResident = true;
            }
            try
            {
                new Autonomy(null, stack.Arbitrator);
            }
            catch (ArgumentNullException)
            {
                nullWorld = true;
            }
            try
            {
                new Autonomy(stack.World, null);
            }
            catch (ArgumentNullException)
            {
                nullArbitrator = true;
            }
            try
            {
                stack.Autonomy.Decide(r1.Id, 0.5);
            }
            catch (ArgumentOutOfRangeException)
            {
                badJitterLow = true;
            }
            try
            {
                stack.Autonomy.Decide(r1.Id, 1.5);
            }
            catch (ArgumentOutOfRangeException)
            {
                badJitterHigh = true;
            }
            try
            {
                stack.Autonomy.DrawAcceptance((ResidentId)424242L, r1.Id);
            }
            catch (ArgumentException)
            {
                unknownAcceptance = true;
            }
            try
            {
                stack.Autonomy.DrawSentiment(r1.Id, (ResidentId)424242L);
            }
            catch (ArgumentException)
            {
                unknownSentiment = true;
            }
            Check(unknownResident && nullWorld && nullArbitrator && badJitterLow
                && badJitterHigh && unknownAcceptance && unknownSentiment,
                "boundary-inputs-throw");
        }

        // 17. Set-to-full remedies: Hygiene 8 restricts to the shower and the
        // expected gain is exactly (100 - current).
        {
            Stack stack = NewStack(42L);
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            SetAllNeeds(stack.World, r1, 80.0f);
            SetAllTraits(stack.World, r1, 0);
            stack.World.SetNeed(Core, r1.Id, Need.Hygiene, 8.0f);
            AutonomyDecision decision = stack.Autonomy.Decide(r1.Id, 1.0);
            CandidateScore shower = FindCandidate(decision, "ACT-HEALTH-03");
            double expectedBenefit = 1.0 + Autonomy.Urgency(8.0f) * (100.0 - 8.0);
            Check(decision.Trigger == "Remedy:Hygiene" && shower != null
                && decision.Candidates.Count == 1
                && Math.Abs(shower.Benefit - expectedBenefit) < 1e-9
                && OracleMatches(decision), "set-to-full-remedy-gain-exact");
        }

        // 18. Path divisor: a 60-minute path halves the score against the same
        // candidate at zero minutes; both values are logged.
        {
            Stack stack = NewStack(42L);
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            SetAllNeeds(stack.World, r1, 80.0f);
            SetAllTraits(stack.World, r1, 0);
            stack.Autonomy.PathMinutes = delegate(ActivityId activity, long? targetId)
            {
                return string.Equals(activity.Code, "ACT-WORK-03", StringComparison.Ordinal) ? 60.0 : 0.0;
            };
            AutonomyDecision decision = stack.Autonomy.Decide(r1.Id,
                new List<AvailableAction>
                {
                    new AvailableAction(new ActivityId("ACT-WORK-03"), null),
                    new AvailableAction(new ActivityId("ACT-EDU-03"), null),
                }.AsReadOnly(), 1.0);
            CandidateScore work = FindCandidate(decision, "ACT-WORK-03");
            CandidateScore edu = FindCandidate(decision, "ACT-EDU-03");
            Check(work != null && edu != null && work.PathMinutes == 60.0
                && edu.PathMinutes == 0.0
                && Math.Abs(work.Score * 2.0 - edu.Score) < 1e-9
                && decision.Chosen.Code == "ACT-EDU-03", "path-minutes-divide-score");
        }

        // 19. Sentiment draws are recorded, bounded and seeded; negative
        // feelings lower the acceptance probability.
        {
            Stack a = NewStack(4242L);
            ResidentRecord a1 = AddResident(a.World, "A1", LifeStage.Adult);
            ResidentRecord a2 = AddResident(a.World, "A2", LifeStage.Adult);
            SetAllTraits(a.World, a1, 0);
            a.World.SetTrait(Core, a1.Id, TraitAxis.Sociable, 30);
            a.World.SetBond(Core, a1.Id, a2.Id, 20.0f, 0.0f);
            bool assigned = a.Autonomy.DrawSentiment(a1.Id, a2.Id);
            SocialDraw logged = a.Autonomy.DrawLog[0];
            double plain = Autonomy.AcceptanceProbability(0.0f, 0.0f, 0, 0);
            double hurt = Autonomy.AcceptanceProbability(0.0f, 0.0f, 0, 2);
            Check(logged.Kind == "Sentiment" && logged.Draw >= 0.0 && logged.Draw < 1.0
                && logged.Probability >= 0.05 && logged.Probability <= 0.95
                && logged.Accepted == assigned && hurt < plain,
                "sentiment-draw-recorded-negative-feelings-lower-acceptance");
        }

        // 20. No static mutable state on the new types (Fast Enter Play Mode).
        {
            Check(!HasStaticMutable(typeof(Autonomy))
                && !HasStaticMutable(typeof(AutonomyDecision))
                && !HasStaticMutable(typeof(CandidateScore))
                && !HasStaticMutable(typeof(SocialDraw)), "no-static-mutable-state");
        }

        if (failures == 0)
        {
            Console.WriteLine("PERSONALITY ALL PASS");
        }
        else
        {
            Console.WriteLine("PERSONALITY FAILURES: " + failures.ToString());
        }
        return failures;
    }

    private static AutonomyDecision PairedDecision(long seed, TraitAxis axis, int trait)
    {
        Stack stack = NewStack(seed);
        ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
        SetAllNeeds(stack.World, r1, 80.0f);
        stack.World.SetNeed(Core, r1.Id, Need.Social, 30.0f);
        stack.World.SetNeed(Core, r1.Id, Need.Fun, 30.0f);
        SetAllTraits(stack.World, r1, 0);
        stack.World.SetTrait(Core, r1.Id, TraitAxis.Active, 50);
        stack.World.SetTrait(Core, r1.Id, axis, trait);
        return stack.Autonomy.Decide(r1.Id, 1.0);
    }

    private static bool HasStaticMutable(Type type)
    {
        FieldInfo[] fields = type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        for (int i = 0; i < fields.Length; i++)
        {
            if (!fields[i].IsInitOnly && !fields[i].IsLiteral)
            {
                return true;
            }
        }
        return false;
    }
}
