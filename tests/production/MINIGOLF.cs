/* MINIGOLF@1 TDD contract: BP-ACT-08 / ACT-SPORT-02, isolated-csharp.
 * Real WorldState, Clock, CommandArbitrator, ActivityExecutor and SaveV1 only.
 * This new suite is required by the MINIGOLF package; no existing suite owns it.
 * Local portions of SEIT-ACT-08-P1..P4/N1..N4, SEIT-IF-EXEC-P5, AC-04/10.
 * UI, art, process restart and whole-case acceptance remain at their SEIT gates.
 *
 * Required public seam in Blockparty.Activities.Minigolf (not present at red):
 *   Minigolf(WorldState, CommandArbitrator, ActivityExecutor, Clock)
 *   RegisterCourse(ObjectId, Hole[3], friction, metresPerSecondPerPower)
 *   ActionInstanceId Start(ObjectId, ResidentId[], bool[] human)
 *   MatchSnapshot GetMatch(ActionInstanceId) -- detached diagnostic DTO
 *   bool SubmitPutt(action, holeIndex, turnIndex, resident, degrees, power)
 *   bool TrySetHuman(action, resident, human); void Leave(action, resident)
 *   void SetUnsafeWeather(bool); void Tick(StateWriter)
 *   static Minigolf FromExecutor(ActivityExecutor) -- restored attached service
 * Hole(tee, cup, minX, maxX, minZ, maxZ, cupRadius), coordinates in metres.
 * Direction 0 = +X, 90 = +Z; friction is m/s^2 in normal-play seconds.
 * HoleIndex is zero-based; TurnIndex identifies a turn and changes on resolution.
 * Snapshots expose the members used below; arrays are copies, never live state.
 * Tick drives NAV as Food.Tick does; do not tick the executor separately.
 * SAVE captures/restores the attached service via the existing GameStack API.
 *
 * Physics values here are synthetic course content, not playability claims:
 * straight unobstructed greens make the stroke oracle independent of product
 * scoring. Power 30 launches at 3 m/s, friction 1 stops at 4.5 m, crossing the
 * 3 m cup. Power zero cannot hole; six such putts must score six.
 * No need-decay service runs, so catalog need deltas are exact in these fixtures.
 * "negative-fixture" corrupts a real detached snapshot to represent an accepted
 * human->autonomous switch; the same fixed-mode oracle must print FAIL/exit 1.
 */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;

public static class MinigolfChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private const StateWriter Exec = StateWriter.ActivityExecutor;
    private static int failures;
    private static bool negativeFixture;

    private sealed class Fixture
    {
        public WorldState World;
        public Clock Clock;
        public CommandArbitrator Arbitrator;
        public ActivityExecutor Executor;
        public AppearanceStore Appearances;
        public Minigolf Golf;
        public ResidentId[] Residents;
        public ObjectId Course;
        public ActionInstanceId Action;
        public Minigolf.MatchSnapshot Match { get { return Golf.GetMatch(Action); } }
        public GameStack Stack()
        {
            return new GameStack(World, Clock, Arbitrator, Executor, Appearances);
        }
    }

    private sealed class Scene : ISceneContent
    {
        public ObjectId Course;
        public bool Missing;
        public void RegisterObjects(ActivityExecutor executor)
        {
            if (!Missing)
                executor.RegisterObject(Course, new[] { new ApproachPose(0, 0, 0) });
        }
    }

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    private static void Require(bool ok, string detail)
    {
        if (!ok) throw new Exception(detail);
    }

    private static void Run(string name, Action test)
    {
        try { test(); }
        catch (Exception error) { Check(false, name + " " + error); }
    }

    private static Fixture NewFixture(bool[] human, double distance = 3,
        double friction = 1, double width = 1000, int fitness = 0)
    {
        Fixture f = new Fixture();
        f.World = WorldState.CreateSeeded(8L, 20261028L, human.Length, null);
        f.Clock = new Clock(f.World);
        f.Clock.SetPreset(AgingPreset.Off);
        f.Arbitrator = new CommandArbitrator(f.World, f.Clock);
        f.Executor = new ActivityExecutor(f.World, f.Arbitrator, f.Clock);
        f.Appearances = new AppearanceStore();
        f.Residents = new ResidentId[human.Length];
        ResidentRecord[] rows = f.World.ResidentsSnapshot();
        for (int i = 0; i < rows.Length; i++)
        {
            f.Residents[i] = rows[i].Id;
            f.World.SetNeed(Core, rows[i].Id, Need.Fun, 40);
            f.World.SetNeed(Core, rows[i].Id, Need.Social, 40);
            // Fitness progress thresholds are STATE's 100*(level+1) contract.
            if (fitness > 0) f.World.AddSkillPoints(Core, rows[i].Id, "Fitness", 50 * fitness * (fitness + 1));
            f.Executor.SetResidentPosition(rows[i].Id, new ApproachPose(0, 0, 0));
            for (int j = 0; j < rows.Length; j++)
                if (i != j) f.World.SetBond(Core, rows[i].Id, rows[j].Id, 0, 0);
        }
        f.Course = f.World.AllocateObject(Core);
        f.Executor.RegisterObject(f.Course, new[] { new ApproachPose(0, 0, 0) });
        f.Golf = new Minigolf(f.World, f.Arbitrator, f.Executor, f.Clock);
        Minigolf.Hole[] holes = new Minigolf.Hole[3];
        for (int i = 0; i < holes.Length; i++)
            holes[i] = new Minigolf.Hole(new ApproachPose(0, 0, 0),
                new ApproachPose((float)distance, 0, 0), -1, 1000, -width, width,
                Math.Min(0.75, distance / 4));
        f.Golf.RegisterCourse(f.Course, holes, friction, 0.1);
        // Deliberately reverse entry order: the product must sort residents and
        // keep each supplied mode associated with its resident, not its index.
        ResidentId[] entry = new ResidentId[human.Length];
        bool[] modes = new bool[human.Length];
        for (int i = 0; i < entry.Length; i++)
        {
            entry[i] = f.Residents[entry.Length - 1 - i];
            modes[i] = human[entry.Length - 1 - i];
        }
        f.Action = f.Golf.Start(f.Course, entry, modes);
        f.Golf.Tick(Exec);
        f.Golf.Tick(Exec);
        Require(f.Match != null, "missing MINIGOLF match after entry");
        return f;
    }

    private static ResidentRecord Resident(Fixture f, int i)
    {
        ResidentRecord row;
        Require(f.World.TryGetResident(f.Residents[i], out row), "fixture resident missing");
        return row;
    }

    private static int Index(Fixture f, ResidentId resident)
    {
        for (int i = 0; i < f.Residents.Length; i++) if (f.Residents[i] == resident) return i;
        throw new Exception("unknown active MINIGOLF participant");
    }

    private static void Steps(Fixture f, int ticks)
    {
        for (int i = 0; i < ticks; i++)
            // One callback per fixed tick even at Fast/Ultra; no wall-clock wait.
            f.Clock.AdvanceRealSeconds(Core, 0.1, () => f.Golf.Tick(Exec));
    }

    private static void Resolve(Fixture f)
    {
        int ticks = 0;
        while (f.Match.Rolling && ticks++ < 81) Steps(f, 1);
        Require(!f.Match.Rolling, "MINIGOLF putt exceeded eight normal-play seconds");
    }

    private static bool Putt(Fixture f, int power, int degrees = 0)
    {
        Minigolf.MatchSnapshot m = f.Match;
        return f.Golf.SubmitPutt(f.Action, m.HoleIndex, m.TurnIndex, m.ActiveResident, degrees, power);
    }

    private static int Total(Minigolf.ParticipantSnapshot p)
    {
        return p.Strokes[0] + p.Strokes[1] + p.Strokes[2];
    }

    private static bool Same(ApproachPose a, ApproachPose b)
    {
        return a.X == b.X && a.Y == b.Y && a.Z == b.Z;
    }

    private static bool Resting(ApproachPose v) { return v.X == 0 && v.Y == 0 && v.Z == 0; }

    private static void Pose(StringBuilder text, ApproachPose p)
    {
        text.Append(p.X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
            .Append(p.Y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
            .Append(p.Z.ToString("R", CultureInfo.InvariantCulture)).Append('|');
    }

    // Compare complete observable checkpoints, not just a score or schema tag.
    private static string Checkpoint(Minigolf.MatchSnapshot m)
    {
        StringBuilder s = new StringBuilder();
        s.Append((long)m.Action).Append('|').Append(m.HoleIndex).Append('|')
            .Append(m.TurnIndex).Append('|').Append((long)m.ActiveResident).Append('|')
            .Append(m.Rolling).Append('|').Append(m.Completed).Append('|').Append(m.Cancelled).Append('|')
            .Append(m.ElapsedGameSeconds).Append('|').Append(m.PuttElapsedGameSeconds).Append('|')
            .Append(m.AimElapsedGameSeconds).Append('|').Append(m.AimDegrees).Append('|')
            .Append(m.AimPower).Append('|').Append(m.PendingConfirmationCount).Append('|')
            .Append(m.RandomState).Append('|');
        foreach (Minigolf.ParticipantSnapshot p in m.Participants)
        {
            s.Append((long)p.Resident).Append('|').Append(p.Human).Append('|').Append(p.Winner).Append('|');
            for (int i = 0; i < 3; i++) s.Append(p.Strokes[i]).Append(':').Append(p.HoleComplete[i]).Append('|');
            Pose(s, p.BallPosition); Pose(s, p.BallVelocity);
        }
        foreach (Minigolf.PuttSnapshot p in m.Putts)
            s.Append((long)p.Resident).Append(':').Append(p.HoleIndex).Append(':').Append(p.TurnIndex)
                .Append(':').Append(p.Degrees).Append(':').Append(p.Power).Append(':')
                .Append(p.RandomDraw.HasValue ? p.RandomDraw.Value.ToString("R", CultureInfo.InvariantCulture) : "human")
                .Append('|');
        return s.ToString();
    }

    private static bool Modes(Minigolf.MatchSnapshot m, bool[] expected)
    {
        if (m.Participants.Length != expected.Length) return false;
        for (int i = 0; i < expected.Length; i++) if (m.Participants[i].Human != expected[i]) return false;
        return true;
    }

    private static void FixedOrderAndModes()
    {
        bool[] modes = { true, false, true };
        Fixture f = NewFixture(modes);
        Check(Modes(f.Match, modes) && f.Match.ActiveResident == f.Residents[0]
            && f.Match.Participants[0].Resident == f.Residents[0]
            && f.Match.Participants[1].Resident == f.Residents[1]
            && f.Match.Participants[2].Resident == f.Residents[2],
            "SEIT-ACT-08-P4-entry-order-and-modes");
        // Finish hole 1, then one putt each on hole 2, as N4 requires.
        for (int i = 0; i < 3; i++)
        {
            if (i == 1) { Steps(f, 10); Resolve(f); }
            else { Require(Putt(f, 30), "human hole 1 putt refused"); Resolve(f); }
        }
        Require(f.Match.HoleIndex == 1, "fixture must reach hole 2");
        Require(Putt(f, 0), "hole 2 human putt refused"); Resolve(f);
        Fixture control = NewFixture(modes);
        for (int i = 0; i < 3; i++)
        {
            if (i == 1) { Steps(control, 10); Resolve(control); }
            else { Require(Putt(control, 30), "control putt refused"); Resolve(control); }
        }
        Require(Putt(control, 0), "control hole 2 putt refused"); Resolve(control);
        string before = Checkpoint(f.Match);
        Check(!Putt(f, 100, 90) && Checkpoint(f.Match) == before,
            "SEIT-ACT-08-N4-human-input-cannot-take-autonomous-turn");
        Steps(f, 10); Resolve(f); Steps(control, 10); Resolve(control);
        before = Checkpoint(f.Match);
        Check(!f.Golf.TrySetHuman(f.Action, f.Residents[2], false) && Checkpoint(f.Match) == before,
            "SEIT-ACT-08-N4-switch-refused-without-turn-stroke-score-or-ball-change");
        Minigolf.MatchSnapshot observed = f.Match;
        if (negativeFixture) observed.Participants[2].Human = false;
        Check(Modes(observed, modes), "SEIT-ACT-08-N4-fixed-mode-oracle-rejects-accepted-switch");
        Steps(f, 10); Steps(control, 10);
        Check(Checkpoint(f.Match) == Checkpoint(control.Match) && f.Match.ActiveResident == f.Residents[2],
            "SEIT-ACT-08-N4-seeded-control-equal-human-still-waits");

        Conflict(f, 1); Steps(f, 1);
        Check(f.Match.Cancelled, "SEIT-ACT-08-P4-conflict-cancels-before-control-change");
        ActionInstanceId old = f.Action;
        // Re-entry must use the same world, not reset the action allocator.
        f.Action = f.Golf.Start(f.Course, new[] { f.Residents[0], f.Residents[1] }, new[] { false, true });
        f.Golf.Tick(Exec); f.Golf.Tick(Exec);
        Check(f.Action != old && Modes(f.Match, new[] { false, true }),
            "SEIT-ACT-08-P4-new-match-new-id-new-entry-modes");
        Complete(f, new[] { 1, 1 });
        Check(f.Match.Completed && Modes(f.Match, new[] { false, true }),
            "SEIT-ACT-08-P4-new-modes-fixed-through-completion");
    }

    private static void Complete(Fixture f, int[] desired)
    {
        int iterations = 0;
        while (!f.Match.Completed && !f.Match.Cancelled && iterations++ < 3000)
        {
            Minigolf.MatchSnapshot m = f.Match;
            if (m.Rolling) { Resolve(f); continue; }
            int i = Index(f, m.ActiveResident);
            Minigolf.ParticipantSnapshot p = m.Participants[i];
            if (!p.Human) { Steps(f, 1); continue; }
            int target = desired[i];
            int power = target == 6 || p.Strokes[m.HoleIndex] < target - 1 ? 0 : 30;
            Require(Putt(f, power), "active human putt refused");
            Resolve(f);
        }
        Require(f.Match.Completed, "MINIGOLF did not complete three holes within bounded strokes/ticks");
    }

    private static void ScoringAndEffects()
    {
        foreach (int count in new[] { 4, 2, 1 })
        {
            bool[] modes = new bool[count];
            for (int i = 0; i < count; i++) modes[i] = true;
            Fixture f = NewFixture(modes);
            int[] target = count == 4 ? new[] { 1, 6, 2, 2 } : count == 2 ? new[] { 2, 2 } : new[] { 1 };
            // Check each actual turn and hole boundary against independent stroke counts.
            for (int hole = 0; hole < 3; hole++)
            {
                for (int stroke = 1; stroke <= 6; stroke++)
                    for (int i = 0; i < count; i++)
                    {
                        if (stroke > target[i]) continue;
                        Minigolf.MatchSnapshot m = f.Match;
                        Check(!m.Completed && m.HoleIndex == hole && m.ActiveResident == f.Residents[i],
                            "SEIT-ACT-08-P3-hole-" + hole + "-stroke-" + stroke + "-resident-" + i + "-ordered-skips-finished");
                        int power = target[i] != 6 && stroke == target[i] ? 30 : 0;
                        Require(Putt(f, power), "scripted putt refused"); Resolve(f);
                        Check(f.Match.Participants[i].Strokes[hole] == stroke
                            && f.Match.Participants[i].HoleComplete[hole] == (stroke == target[i]),
                            "SEIT-ACT-08-P3-counted-stroke-and-hole-completion");
                        if (!f.Match.Completed)
                            for (int j = 0; j < count; j++)
                                Check(Resident(f, j).GetNeed(Need.Fun) == 40 && Resident(f, j).GetNeed(Need.Social) == 40,
                                    "SEIT-IF-EXEC-P5-no-early-completion-reward");
                    }
                if (hole < 2) Check(f.Match.HoleIndex == hole + 1, "SEIT-ACT-08-P3-next-hole-only-after-all-finish");
            }
            Check(f.Match.Completed && !f.Match.Cancelled, "SEIT-ACT-08-P1-three-holes-terminal");
            for (int i = 0; i < count; i++)
            {
                Minigolf.ParticipantSnapshot p = f.Match.Participants[i];
                bool winner = count != 4 || i == 0;
                Check(Total(p) == target[i] * 3 && Total(p) >= 3 && Total(p) <= 18 && p.Winner == winner,
                    "SEIT-ACT-08-P3-totals-3-18-6-6-or-tied-minima-solo-" + count + "-" + i);
                ResidentRecord r = Resident(f, i);
                int points = 0, proud = 0;
                foreach (SkillEntry skill in r.Skills) if (skill.SkillId == "Fitness") points = skill.Points;
                foreach (Feeling feeling in r.Feelings) if (feeling.Kind == FeelingKind.Proud && feeling.Intensity == 1) proud++;
                Check(r.GetNeed(Need.Fun) == 65 && r.GetNeed(Need.Social) == 50 && points == 5 && proud == (winner ? 1 : 0),
                    "SEIT-ACT-08-P1-completion-effects-" + i);
                foreach (PairBond bond in r.Bonds) Check(bond.Friendship == 2, "SEIT-ACT-08-P3-friendship-plus-two-both-directions-once");
                Check(f.Arbitrator.GetCurrent(r.Id) == null, "SEIT-AC-04-completion-releases-dependent-action");
            }
            Check(!f.Executor.GetHold(f.Course, 0).HasValue, "SEIT-AC-10-completion-releases-course");
            HashSet<string> keys = new HashSet<string>();
            HashSet<long> recipients = new HashSet<long>();
            HashSet<string> pairs = new HashSet<string>();
            foreach (EffectLedgerEntry row in f.World.EffectLedger)
                if (row.Action == f.Action)
                {
                    string recipient = (long)row.Recipient.First + ":" + (row.Recipient.Second.HasValue ? ((long)row.Recipient.Second.Value).ToString() : "single");
                    Check(row.State == LedgerState.Committed && keys.Add(row.EffectKind + ":" + recipient),
                        "SEIT-IF-EXEC-P5-shared-match-committed-unique-effect-key");
                    if (row.Recipient.Second.HasValue) pairs.Add(recipient); else recipients.Add((long)row.Recipient.First);
                }
            Check(recipients.Count == count && pairs.Count == count * (count - 1) / 2,
                "SEIT-IF-EXEC-P5-participant-and-unordered-pair-ledger-coverage");
            int worldHash = f.World.ComputeStateHash(), ledger = f.World.EffectLedger.Count;
            string terminal = Checkpoint(f.Match);
            f.Golf.Leave(f.Action, f.Residents[0]); f.Golf.Tick(Exec); f.Golf.Tick(Exec);
            Check(Checkpoint(f.Match) == terminal && f.World.ComputeStateHash() == worldHash && f.World.EffectLedger.Count == ledger,
                "SEIT-ACT-08-P1-terminal-replay-no-second-effects");
        }
    }

    private static void Autonomous()
    {
        foreach (SimulationSpeed speed in new[] { SimulationSpeed.Normal, SimulationSpeed.Fast })
            foreach (int fitness in new[] { 0, 10 })
                foreach (double distance in new[] { 0.1, 3.0, 20.0 })
                {
                    Fixture f = NewFixture(new[] { false }, distance, 1, 1000, fitness);
                    f.Clock.SetSpeed(speed);
                    int multiplier = (int)Clockwork.SpeedMultiplier(speed);
                    Steps(f, speed == SimulationSpeed.Normal ? 9 : 3);
                    Check(f.Match.Putts.Length == 0 && !f.Match.Rolling, "SEIT-ACT-08-P2-no-auto-putt-before-60-game-seconds");
                    Steps(f, 1);
                    Require(f.Match.Putts.Length == 1, "autonomous putt missing after 60 game seconds");
                    Minigolf.PuttSnapshot putt = f.Match.Putts[0];
                    int power = distance == 0.1 ? 5 : distance == 3 ? 30 : 100;
                    Require(putt.RandomDraw.HasValue, "autonomous angular draw not recorded");
                    double draw = putt.RandomDraw.Value;
                    double error = (draw * 2 - 1) * (10 - fitness);
                    int angle = ((int)Math.Round(error / 5) * 5 % 360 + 360) % 360;
                    Check(putt.Power == power && draw >= 0 && draw < 1 && putt.Degrees == angle,
                        "SEIT-ACT-08-P2-distance-power-clamps-fitness-error-recorded-draw-" + distance + "-" + fitness + "-" + multiplier);
                    Console.WriteLine("DRAW seed=20261028 fitness=" + fitness + " distance=" + distance + " draw=" + draw.ToString("R", CultureInfo.InvariantCulture));
                    Complete(f, new[] { 1 });
                    Check(Total(f.Match.Participants[0]) >= 3 && Total(f.Match.Participants[0]) <= 18,
                        "SEIT-ACT-08-P2-autonomous-three-hole-match-" + speed);
                    foreach (Minigolf.PuttSnapshot row in f.Match.Putts)
                        Check(row.RandomDraw.HasValue && row.RandomDraw.Value >= 0 && row.RandomDraw.Value < 1,
                            "SEIT-ACT-08-P2-every-autonomous-putt-has-draw");
                }
    }

    private static void SharedClockAndPhysics()
    {
        Fixture wait = NewFixture(new[] { true });
        Steps(wait, 700);
        Check(wait.World.GameSeconds == 4200 && !wait.Match.Completed && wait.Match.Putts.Length == 0
            && Total(wait.Match.Participants[0]) == 0 && Resident(wait, 0).GetNeed(Need.Fun) == 40,
            "SEIT-ACT-08-N1-human-70-minutes-no-deadline-or-fabricated-score");
        Fixture f = NewFixture(new[] { true }, 900, 0);
        Require(Putt(f, 100), "forced-rest launch refused");
        Steps(f, 79);
        Check(f.Match.Rolling && !Resting(f.Match.Participants[0].BallVelocity)
            && f.Match.PuttElapsedGameSeconds == 474, "SEIT-ACT-08-N1-still-moving-before-eight-normal-seconds");
        ApproachPose before = f.Match.Participants[0].BallPosition;
        Steps(f, 1);
        ApproachPose after = f.Match.Participants[0].BallPosition;
        Check(!f.Match.Rolling && Resting(f.Match.Participants[0].BallVelocity)
            && after.X >= before.X && after.X > 0 && after.X <= 1000 && f.Match.Participants[0].Strokes[0] == 1,
            "SEIT-ACT-08-N1-forced-rest-at-480-game-seconds-current-legal-position");

        Fixture normal = NewFixture(new[] { true }, 900, 0);
        Fixture ultra = NewFixture(new[] { true }, 900, 0);
        Require(Putt(normal, 100) && Putt(ultra, 100), "speed fixture launch refused");
        Steps(normal, 1); Steps(ultra, 1);
        ultra.Clock.Pause();
        string paused = Checkpoint(ultra.Match);
        Steps(ultra, 300);
        Check(Checkpoint(ultra.Match) == paused, "SEIT-ACT-08-N1-pause-freezes-physics-and-timers");
        ultra.Clock.Resume(); ultra.Clock.SetSpeed(SimulationSpeed.Ultra);
        Steps(normal, 7); Steps(ultra, 1);
        Check(Checkpoint(normal.Match) == Checkpoint(ultra.Match), "SEIT-ACT-08-N1-ultra-shared-delta-equivalent-to-seven-normal-ticks");

        Fixture pending = NewFixture(new[] { true });
        pending.Clock.Pause();
        Require(Putt(pending, 10) && Putt(pending, 30), "paused confirmations refused");
        Check(pending.Match.PendingConfirmationCount == 1 && pending.Match.Putts.Length == 0,
            "SEIT-ACT-08-N1-pause-one-replaceable-confirmation");
        pending.Clock.Resume(); Steps(pending, 1);
        Check(pending.Match.PendingConfirmationCount == 0 && pending.Match.Putts.Length == 1 && pending.Match.Putts[0].Power == 30,
            "SEIT-ACT-08-N1-resume-consumes-latest-confirmation-once");

        Fixture bounds = NewFixture(new[] { true }, 3, 0, 1);
        Require(Putt(bounds, 100, 90), "out-of-bounds launch refused"); Resolve(bounds);
        Check(Same(bounds.Match.Participants[0].BallPosition, new ApproachPose(0, 0, 0))
            && bounds.Match.Participants[0].Strokes[0] == 1 && !bounds.Match.Participants[0].HoleComplete[0],
            "SEIT-ACT-08-P3-out-of-bounds-returns-to-rest-counts-one-stroke");
    }

    private static void Conflict(Fixture f, int i)
    {
        ResidentRecord row = Resident(f, i);
        CommandEnvelope command = new CommandEnvelope(f.World.AllocateCommand(Core), f.World.Epoch,
            f.World.GameSeconds, CommandSource.PlayerHuman, row.ResidentSequence + 1u,
            CommandKind.DirectActivity, row.Id, new ActivityId("ACT-HEALTH-02"), null,
            new CommandParameters.Direct(QueueMode.Immediate));
        Require(f.Arbitrator.Submit(command).Accepted, "real conflicting command refused");
    }

    private static void Cancellation()
    {
        foreach (string reason in new[] { "conflict", "weather", "leave", "death", "target", "zero" })
        {
            Fixture f = NewFixture(new[] { true, true, true });
            if (reason != "zero")
            {
                for (int i = 0; i < 3; i++) { Require(Putt(f, 30), "hole 1 putt refused"); Resolve(f); }
                for (int i = 0; i < 2; i++) { Require(Putt(f, 0), "hole 2 putt refused"); Resolve(f); }
            }
            Minigolf.MatchSnapshot before = f.Match;
            Check(f.Executor.GetHold(f.Course, 0).HasValue, "SEIT-ACT-08-N3-whole-course-reserved-before-cancel");
            if (reason == "conflict" || reason == "zero") Conflict(f, 1);
            else if (reason == "weather") f.Golf.SetUnsafeWeather(true);
            else if (reason == "leave") f.Golf.Leave(f.Action, f.Residents[1]);
            else if (reason == "death") f.World.SetAlive(Core, f.Residents[1], false);
            else f.Executor.RemoveObject(f.Course);
            Check(!f.Match.Cancelled, "SEIT-ACT-08-N3-cancellation-waits-for-simulation-tick-" + reason);
            Steps(f, 1);
            Check(f.Match.Cancelled && !f.Match.Completed && !f.Executor.GetHold(f.Course, 0).HasValue,
                "SEIT-ACT-08-N3-cancelled-and-course-released-" + reason);
            for (int i = 0; i < 3; i++)
            {
                Minigolf.ParticipantSnapshot p = f.Match.Participants[i];
                ResidentRecord r = Resident(f, i);
                float fun = reason != "zero" && r.IsAlive ? 50 : 40;
                bool retained = !p.Winner;
                for (int hole = 0; hole < 3; hole++)
                    retained &= p.Strokes[hole] == before.Participants[i].Strokes[hole]
                        && p.HoleComplete[hole] == before.Participants[i].HoleComplete[hole];
                Check(retained && r.GetNeed(Need.Fun) == fun && r.GetNeed(Need.Social) == 40
                    && r.Skills.Count == 0 && r.Feelings.Count == 0,
                    "SEIT-ACT-08-N3-incomplete-scores-living-fun-only-" + reason + "-" + i);
                foreach (PairBond bond in r.Bonds) Check(bond.Friendship == 0, "SEIT-ACT-08-N3-no-friendship-completion-effect");
                ActionEntry current = f.Arbitrator.GetCurrent(r.Id);
                Check(current == null || current.Activity.Code != "ACT-SPORT-02", "SEIT-ACT-08-N3-dependent-actions-released");
                Check(Same(f.Executor.GetResidentPosition(r.Id), new ApproachPose(0, 0, 0)), "SEIT-ACT-08-N3-no-teleport-on-exit");
            }
            int ledger = f.World.EffectLedger.Count, hash = f.World.ComputeStateHash();
            string terminal = Checkpoint(f.Match);
            f.Golf.Leave(f.Action, f.Residents[0]); f.Golf.Tick(Exec);
            Check(Checkpoint(f.Match) == terminal && f.World.EffectLedger.Count == ledger && f.World.ComputeStateHash() == hash,
                "SEIT-ACT-08-N3-repeat-cancel-inert");
        }
        Fixture stale = NewFixture(new[] { true });
        Minigolf.MatchSnapshot old = stale.Match;
        Require(Putt(stale, 30), "stale-input fixture launch refused"); Resolve(stale);
        string checkpoint = Checkpoint(stale.Match);
        Check(!stale.Golf.SubmitPutt(stale.Action, old.HoleIndex, old.TurnIndex, old.ActiveResident, 0, 30)
            && Checkpoint(stale.Match) == checkpoint, "SEIT-ACT-08-N3-stale-hole-turn-input-rejected-without-stroke");
    }

    private static Fixture Load(Fixture original, string directory, bool missing = false)
    {
        SaveLoadResult loaded = SaveV1.LoadGame(directory, new Scene { Course = original.Course, Missing = missing });
        Require(loaded.Ok, "public save load failed: " + loaded.Notice);
        Fixture f = new Fixture
        {
            World = loaded.Game.World, Clock = loaded.Game.Clock, Arbitrator = loaded.Game.Arbitrator,
            Executor = loaded.Game.Executor, Appearances = loaded.Game.Appearances,
            Residents = original.Residents, Course = original.Course, Action = original.Action,
        };
        f.Golf = Minigolf.FromExecutor(f.Executor);
        Require(f.Golf != null, "SAVE-V2 did not restore MINIGOLF service");
        return f;
    }

    private static void SaveRoundTrip(string root)
    {
        Fixture f = NewFixture(new[] { false });
        int setupTicks = 0;
        while (f.Match.HoleIndex == 0 && !f.Match.Completed && setupTicks++ < 1000) { Steps(f, 1); Resolve(f); }
        Require(f.Match.HoleIndex == 1, "save fixture must reach hole 2");
        for (int aim = 0; !f.Match.Rolling && aim < 11; aim++) Steps(f, 1);
        Steps(f, 2);
        Require(f.Match.Rolling && f.Match.PuttElapsedGameSeconds > 0, "save fixture not mid-putt");
        f.Clock.Pause();
        string checkpoint = Checkpoint(f.Match);
        string mid = Path.Combine(root, "mid-putt");
        Require(SaveV1.SaveGame(f.Stack(), mid).Ok, "public mid-putt save failed");
        Fixture restored = Load(f, mid);
        Check(restored.Clock.Paused && Checkpoint(restored.Match) == checkpoint,
            "SEIT-ACT-08-N2-mid-putt-public-save-restores-identical-physics-inputs-draws-and-progress");
        Steps(restored, 30);
        Check(Checkpoint(restored.Match) == checkpoint, "SEIT-ACT-08-N2-paused-load-neither-relaunches-nor-rewards");
        f.Clock.Resume(); restored.Clock.Resume();
        int ticks = 0;
        while (!f.Match.Completed && ticks++ < 3000)
        {
            Steps(f, 1); Steps(restored, 1);
            Require(Checkpoint(f.Match) == Checkpoint(restored.Match), "resumed MINIGOLF trajectory/draw diverged from uninterrupted run");
        }
        Check(f.Match.Completed && restored.Match.Completed
            && Resident(f, 0).GetNeed(Need.Fun) == Resident(restored, 0).GetNeed(Need.Fun)
            && f.World.EffectLedger.Count == restored.World.EffectLedger.Count,
            "SEIT-ACT-08-N2-identical-continuation-no-resampling-or-extra-stroke");
        string done = Path.Combine(root, "completed");
        Require(SaveV1.SaveGame(restored.Stack(), done).Ok, "public completed save failed");
        Fixture again = Load(restored, done);
        int hash = again.World.ComputeStateHash(), ledger = again.World.EffectLedger.Count;
        again.Golf.Leave(again.Action, again.Residents[0]); again.Golf.Tick(Exec);
        Check(Checkpoint(again.Match) == Checkpoint(restored.Match) && again.World.ComputeStateHash() == hash
            && again.World.EffectLedger.Count == ledger && Resident(again, 0).GetNeed(Need.Fun) == 65,
            "SEIT-ACT-08-N2-completed-save-reload-effects-once");

        Fixture invalid = Load(f, mid, true);
        Check(!invalid.Match.Cancelled && Resident(invalid, 0).GetNeed(Need.Fun) == 40,
            "SEIT-ACT-08-N2-missing-course-paused-restore-defers-cancellation");
        invalid.Clock.Resume(); Steps(invalid, 1);
        Check(invalid.Match.Cancelled && !invalid.Match.Completed && Resident(invalid, 0).GetNeed(Need.Fun) == 50,
            "SEIT-ACT-08-N2-missing-course-resume-cancels-once");

        Fixture pending = NewFixture(new[] { true });
        pending.Clock.Pause(); Require(Putt(pending, 30), "save pending confirmation refused");
        string queued = Path.Combine(root, "pending-confirmation");
        Require(SaveV1.SaveGame(pending.Stack(), queued).Ok, "public pending save failed");
        Fixture queuedLoad = Load(pending, queued);
        Check(Checkpoint(queuedLoad.Match) == Checkpoint(pending.Match), "SEIT-ACT-08-N2-pending-turn-confirmation-durable");
        queuedLoad.Clock.Resume(); Steps(queuedLoad, 1);
        Check(queuedLoad.Match.Putts.Length == 1 && queuedLoad.Match.PendingConfirmationCount == 0,
            "SEIT-ACT-08-N2-restored-pending-confirmation-launches-once");
    }

    private static void AcceptedWriterTick()
    {
        string expected = null;
        foreach (StateWriter writer in new[] { Exec, Core, StateWriter.CommandArbitrator })
        {
            Fixture f = NewFixture(new[] { true });
            for (int hole = 0; hole < 3; hole++)
            {
                Require(Putt(f, 30), "writer-contract putt refused");
                for (int tick = 0; f.Match.Rolling && tick < 81; tick++)
                    f.Clock.AdvanceRealSeconds(Core, 0.1, () => f.Golf.Tick(writer));
                Require(!f.Match.Rolling, "writer-contract putt did not resolve");
            }
            string actual = f.World.ComputeStateHash() + "|" + Checkpoint(f.Match);
            Check(f.Match.Completed && !f.Match.Cancelled
                && Resident(f, 0).GetNeed(Need.Fun) == 65
                && (expected == null || actual == expected),
                "MINIGOLF-Tick-" + writer + "-matches-executor-path");
            if (expected == null) expected = actual;
        }
    }

    public static int Main(string[] args)
    {
        negativeFixture = args.Length == 1 && args[0] == "negative-fixture";
        string root = Path.Combine(Path.GetTempPath(), "blockparty-minigolf-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            Run("fixed-order-and-control-modes", FixedOrderAndModes);
            Run("three-hole-scoring-and-effects", ScoringAndEffects);
            Run("autonomous-formula-and-draws", Autonomous);
            Run("shared-clock-and-forced-rest", SharedClockAndPhysics);
            Run("cancellation-and-stale-input", Cancellation);
            Run("public-save-mid-putt", () => SaveRoundTrip(root));
            Run("minigolf-tick-writer-contract", AcceptedWriterTick);
        }
        catch (Exception error) { Check(false, "MINIGOLF-harness " + error); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        if (failures == 0) Console.WriteLine("MINIGOLF ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
