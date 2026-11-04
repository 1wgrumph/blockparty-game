/* RELATION@1 red contract, BP-REL-01..05 and BP-SEC-05.
 * Product file: Assets/Blockparty/Simulation/Relationships.cs; namespace
 * Blockparty.Simulation. Required new public API (C# 9):
 *   sealed class Relationships {
 *     Relationships(WorldState world);
 *     RelationshipResult Complete(StateWriter writer, ActivityId activity,
 *       ResidentId initiator, ResidentId recipient, double? acceptanceDraw = null,
 *       double? loserDraw = null, double? initiatorSentimentDraw = null,
 *       double? recipientSentimentDraw = null);
 *     RelationshipResult Remember(StateWriter writer, ResidentId resident,
 *       EventId sourceEvent, EventId? learnedByChat = null);
 *     MemoryEntry Explain(ResidentId resident, EventId sourceEvent);
 *     void Tick(StateWriter writer);
 *   }
 *   sealed class RelationshipResult {
 *     bool Applied; string Reason; EventId Event; IReadOnlyList<SocialDraw> Draws;
 *   } // Fields or getters are acceptable; Draws reuses PERSONALITY's SocialDraw.
 * Complete is the post-activity completion boundary, not a command/navigation
 * substitute. Tests exercise ACT-REL-01/03/04. Accepted completion records one
 * factual WorldEvent and participant memories for significant events. Argument
 * uses Kind "argument", records pre-effect loser/sentiment probabilities and
 * draws with SocialDraw.Kind "Loser"/"Sentiment"; a romance response uses
 * "Acceptance". Each result owns its draw snapshot; null draws use seeded RNG.
 * Rejected romance is a completed factual rejection (Applied=true), not invalid
 * input: Romance -2, Embarrassed 1 under the per-participant sentiment rule.
 * Invalid input returns Applied=false and a nonempty Reason before mutation.
 * Remember accepts factual participation or a completed Chat learning edge;
 * Chat relays the speaker's latest known significant event BEFORE that Chat.
 * LearnedBy is "participant" or "told-by:<decimal Chat EventId>". The latter
 * refers to the actual completed Chat event; original participants stay intact.
 * Explain returns a detached MemoryEntry projection, null for unknown/unlearned
 * events. Tick expires feelings at GameSeconds >= ExpiryGameSeconds, never from
 * wall time. Mutation uses the existing StateWriter sole-writer boundary.
 * Existing APIs: WorldState/records, Clock, Autonomy, CommandArbitrator,
 * IResidentDirectory, ActivityId/AvailableAction and typed IDs from
 * Blockparty.Contracts.
 * Resource group: isolated-csharp; no editor, display, ports or filesystem I/O.
 * New module is required by the package's standalone runner convention.
 * These are local oracle slices, not whole SEIT completion or UI acceptance.
 */
using System;
using System.Collections.Generic;
using System.Reflection;
using Blockparty.Contracts;
using Blockparty.Simulation;

public static class RelationChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private const long Day = 86400L;
    private static int failures;

    private sealed class Fixture
    {
        public WorldState World;
        public Relationships Relations;
        public ResidentRecord A;
        public ResidentRecord B;
        public ResidentRecord C;

        public Fixture(long seed = 42L)
        {
            World = WorldState.CreateSeeded(7L, seed, 0, null);
            HomeRecord home = World.AllocateHome(Core, 8);
            HouseholdRecord household = World.AllocateHousehold(Core, "H", home.Id);
            A = World.AllocateResident(Core, "R1", LifeStage.Adult, household.Id, home.Id);
            B = World.AllocateResident(Core, "R2", LifeStage.Adult, household.Id, home.Id);
            C = World.AllocateResident(Core, "R3", LifeStage.Adult, household.Id, home.Id);
            foreach (ResidentRecord resident in new[] { A, B, C })
            {
                World.SetNeed(Core, resident.Id, Need.Social, 50.0f);
                World.SetNeed(Core, resident.Id, Need.Comfort, 50.0f);
                World.SetTrait(Core, resident.Id, TraitAxis.Sociable, 0);
            }
            World.SetBond(Core, A.Id, B.Id, 10.0f, 25.0f);
            World.SetBond(Core, B.Id, A.Id, 10.0f, 25.0f);
            Relations = new Relationships(World);
        }

        public RelationshipResult Complete(string code, double? acceptance = 0.0,
            double? loser = 0.9, double? first = 0.99, double? second = 0.01)
        {
            return Relations.Complete(Core, new ActivityId(code), A.Id, B.Id,
                acceptance, loser, first, second);
        }
    }

    private static void Check(bool condition, string name)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
        if (!condition) failures++;
    }

    private static PairBond Bond(ResidentRecord resident, ResidentId other)
    {
        foreach (PairBond bond in resident.Bonds)
            if (bond.Other == other) return bond;
        throw new InvalidOperationException("Fixture bond missing");
    }

    private static Feeling FindFeeling(ResidentRecord resident, FeelingKind kind, EventId source)
    {
        foreach (Feeling feeling in resident.Feelings)
            if (feeling.Kind == kind && feeling.SourceEvent == source) return feeling;
        return null;
    }

    private static MemoryEntry Memory(ResidentRecord resident, EventId source)
    {
        foreach (MemoryEntry memory in resident.Memories)
            if (memory.Event == source) return memory;
        return null;
    }

    private static WorldEvent Event(WorldState world, EventId id)
    {
        foreach (WorldEvent entry in world.Events)
            if (entry.Event == id) return entry;
        return null;
    }

    private static bool Contains(IReadOnlyList<ResidentId> residents, ResidentId id)
    {
        foreach (ResidentId resident in residents)
            if (resident == id) return true;
        return false;
    }

    private static SocialDraw Draw(RelationshipResult result, string kind, ResidentId resident)
    {
        foreach (SocialDraw draw in result.Draws)
            if (draw.Kind == kind && draw.Resident == resident) return draw;
        return null;
    }

    private static bool Near(double actual, double expected)
    {
        return Math.Abs(actual - expected) <= 1e-6;
    }

    private static bool SameMemory(MemoryEntry left, MemoryEntry right)
    {
        if (left == null || right == null || left.Event != right.Event
            || left.Kind != right.Kind || left.GameSeconds != right.GameSeconds
            || left.Place != right.Place || left.OutcomeCode != right.OutcomeCode
            || left.LearnedBy != right.LearnedBy
            || left.Participants.Count != right.Participants.Count) return false;
        for (int i = 0; i < left.Participants.Count; i++)
            if (left.Participants[i] != right.Participants[i]) return false;
        return true;
    }

    private static void RefusedUnchanged(Fixture f, Func<RelationshipResult> operation, string name)
    {
        int hash = f.World.ComputeStateHash();
        ulong rng = f.World.RngState;
        long nextEvent = f.World.NextEventId;
        int eventCount = f.World.EventCount;
        RelationshipResult result = operation();
        Check(!result.Applied && !string.IsNullOrWhiteSpace(result.Reason)
            && hash == f.World.ComputeStateHash() && rng == f.World.RngState
            && nextEvent == f.World.NextEventId && eventCount == f.World.EventCount, name);
    }

    private static void SeparateKinds()
    {
        Fixture f = new Fixture();
        bool applied = true;
        for (int i = 0; i < 3; i++) applied &= f.Complete("ACT-REL-01").Applied;
        Check(applied && Bond(f.A, f.B.Id).Friendship == 19.0f
            && Bond(f.B, f.A.Id).Friendship == 19.0f
            && Bond(f.A, f.B.Id).Romance == 25.0f && Bond(f.B, f.A.Id).Romance == 25.0f
            && f.A.Family.Count == 0 && f.B.Family.Count == 0,
            "SEIT-REL-01-P1.three-chats-only-friendship");

        f = new Fixture();
        f.World.AddFamilyLink(Core, f.A.Id, f.B.Id, "sibling");
        FamilyLink family = f.A.Family[0].Clone();
        Check(f.Complete("ACT-REL-01").Applied && f.A.Family.Count == 1
            && f.A.Family[0].Other == family.Other && f.A.Family[0].Kind == family.Kind,
            "SEIT-REL-01-P1.chat-preserves-explicit-family");

        f = new Fixture();
        Check(f.Complete("ACT-REL-03").Applied
            && Bond(f.A, f.B.Id).Romance == 30.0f && Bond(f.B, f.A.Id).Romance == 30.0f
            && Bond(f.A, f.B.Id).Friendship == 10.0f && Bond(f.B, f.A.Id).Friendship == 10.0f
            && f.A.Family.Count == 0 && f.B.Family.Count == 0,
            "SEIT-REL-01-N1.romance-never-rewrites-friendship-or-creates-spouse");
        f.World.SetBond(Core, f.A.Id, f.B.Id, 10.0f, 90.0f);
        f.World.SetAlive(Core, f.A.Id, false);
        Check(f.A.Family.Count == 0 && f.B.Family.Count == 0,
            "SEIT-REL-01-N1.high-romance-death-does-not-infer-spouse-local");

        f = new Fixture();
        f.World.SetBond(Core, f.A.Id, f.B.Id, 99.0f, 25.0f);
        Check(f.Complete("ACT-REL-01").Applied && Bond(f.A, f.B.Id).Friendship == 100.0f
            && Bond(f.A, f.B.Id).Romance == 25.0f,
            "SEIT-REL-01-P1.friendship-clamps-independently");
    }

    private static void IndividualFeelings()
    {
        Fixture f = new Fixture();
        RelationshipResult result = f.Complete("ACT-REL-04");
        Feeling hurt = FindFeeling(f.B, FeelingKind.Hurt, result.Event);
        SocialDraw loser = Draw(result, "Loser", f.A.Id);
        SocialDraw sentiment = Draw(result, "Sentiment", f.B.Id);
        Check(result.Applied && Event(f.World, result.Event) != null
            && hurt != null && hurt.Other == f.A.Id && hurt.Intensity == 2
            && hurt.ExpiryGameSeconds == f.World.GameSeconds + 4L * Day
            && f.A.Feelings.Count == 0 && loser != null && Near(loser.Probability, 0.5)
            && loser.Draw == 0.9 && sentiment != null && Near(sentiment.Probability, 0.525)
            && sentiment.Draw == 0.01 && sentiment.Accepted
            && Memory(f.A, result.Event) != null && Memory(f.B, result.Event) != null,
            "SEIT-REL-02-P1.argument-asymmetric-feeling-same-event-pre-effect-draws");

        // Fresh state per witness, including pre-existing unrelated Angry.
        foreach (double draw in new[] { 0.01, 0.5 })
        {
            f = LowSentimentFixture(42L);
            result = f.Complete("ACT-REL-04", 0.0, 0.9, 0.99, draw);
            sentiment = Draw(result, "Sentiment", f.B.Id);
            Check(result.Applied && sentiment != null && Near(sentiment.Probability, 0.05)
                && sentiment.Draw == draw && sentiment.Accepted == (draw < 0.05)
                && (FindFeeling(f.B, FeelingKind.Hurt, result.Event) != null) == (draw < 0.05)
                && FindFeeling(f.A, FeelingKind.Hurt, result.Event) == null
                && f.A.Feelings.Count == 1 && f.B.Feelings.Count == (draw < 0.05 ? 2 : 1),
                "SEIT-REL-02-N1.controlled-floor-witness-" + draw.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        for (long seed = 0L; seed < 50L; seed++)
        {
            f = LowSentimentFixture(seed);
            result = f.Complete("ACT-REL-04", null, null, null, null);
            loser = Draw(result, "Loser", f.A.Id);
            ResidentRecord eligible = loser != null && loser.Draw < 0.5 ? f.A : f.B;
            ResidentRecord ineligible = eligible == f.A ? f.B : f.A;
            sentiment = Draw(result, "Sentiment", eligible.Id);
            Check(result.Applied && loser != null && Near(loser.Probability, 0.5)
                && loser.Draw >= 0.0 && loser.Draw < 1.0
                && sentiment != null && Near(sentiment.Probability, 0.05)
                && sentiment.Draw >= 0.0 && sentiment.Draw < 1.0
                && sentiment.Accepted == (sentiment.Draw < 0.05)
                && (FindFeeling(eligible, FeelingKind.Hurt, result.Event) != null) == sentiment.Accepted
                && FindFeeling(ineligible, FeelingKind.Hurt, result.Event) == null,
                "SEIT-REL-02-N1.seeded-independent-eligibility-and-probability-" + seed);
            if (loser != null && sentiment != null)
                Console.WriteLine("DRAW seed=" + seed + " event=" + (long)result.Event
                    + " loser=" + loser.Draw.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    + " sentiment=" + sentiment.Draw.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static Fixture LowSentimentFixture(long seed)
    {
        Fixture f = new Fixture(seed);
        WorldEvent prior = f.World.AppendEvent(Core, "argument", f.A.Id, "prior argument");
        foreach (ResidentRecord resident in new[] { f.A, f.B })
        {
            ResidentId other = resident == f.A ? f.B.Id : f.A.Id;
            f.World.SetTrait(Core, resident.Id, TraitAxis.Sociable, -50);
            f.World.SetNeed(Core, resident.Id, Need.Social, 0.0f);
            f.World.SetBond(Core, resident.Id, other, -100.0f, 0.0f);
            f.World.AddFeeling(Core, resident.Id, FeelingKind.Angry, prior.Event, other, 1, Day);
        }
        return f;
    }

    private static void FactualMemories()
    {
        Fixture f = new Fixture();
        RelationshipResult argument = f.Complete("ACT-REL-04");
        MemoryEntry original = Memory(f.A, argument.Event);
        WorldEvent recorded = Event(f.World, argument.Event);
        Check(argument.Applied && recorded != null && recorded.Kind == "argument"
            && recorded.Actor == f.A.Id && original != null && original.Kind == "argument"
            && original.GameSeconds == f.World.GameSeconds && original.Place == f.A.Home
            && original.Participants.Count == 2 && Contains(original.Participants, f.A.Id)
            && Contains(original.Participants, f.B.Id) && original.OutcomeCode.Length > 0
            && original.LearnedBy == "participant",
            "SEIT-REL-03-P1.participant-memory-has-factual-event-fields");

        f.World.SetGameSeconds(Core, 60L);
        RelationshipResult chat = f.Relations.Complete(Core, new ActivityId("ACT-REL-01"),
            f.A.Id, f.C.Id, 0.0, 0.9, 0.99, 0.99);
        MemoryEntry learned = Memory(f.C, argument.Event);
        string edge = "told-by:" + ((long)chat.Event).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Check(chat.Applied && Event(f.World, chat.Event) != null && learned != null
            && original != null && learned.Event == original.Event && learned.Kind == original.Kind
            && learned.GameSeconds == original.GameSeconds && learned.Place == original.Place
            && learned.OutcomeCode == original.OutcomeCode && learned.LearnedBy == edge
            && Contains(learned.Participants, f.A.Id) && Contains(learned.Participants, f.B.Id)
            && !Contains(learned.Participants, f.C.Id),
            "SEIT-REL-03-N1.chat-relays-original-event-with-learning-edge-not-participation");

        RefusedUnchanged(f, () => f.Relations.Remember(Core, f.C.Id, (EventId)999999L),
            "SEIT-REL-03-N1.invented-event-refused-with-reason-no-state-change");
        Fixture fresh = new Fixture();
        RelationshipResult source = fresh.Complete("ACT-REL-04");
        RefusedUnchanged(fresh, () => fresh.Relations.Remember(Core, fresh.C.Id, source.Event),
            "SEIT-REL-03-N1.unobserved-event-refused-with-reason-no-state-change");
        RefusedUnchanged(fresh, () => fresh.Relations.Remember(Core, fresh.C.Id, source.Event, source.Event),
            "SEIT-REL-03-N1.non-chat-learning-edge-refused-no-state-change");

        // Local three-game-day audit; no soak/runtime acceptance is claimed.
        for (long day = 1L; day <= 3L; day++)
        {
            f.World.SetGameSeconds(Core, day * Day);
            f.Complete("ACT-REL-04");
            f.Relations.Tick(Core);
        }
        bool joined = true;
        int count = 0;
        foreach (ResidentRecord resident in f.World.ResidentsSnapshot())
        foreach (MemoryEntry memory in resident.Memories)
        {
            count++;
            WorldEvent entry = Event(f.World, memory.Event);
            joined &= entry != null && entry.GameSeconds == memory.GameSeconds
                && memory.OutcomeCode.Length > 0 && memory.Participants.Count > 0
                && (memory.Kind != "argument" || entry != null && entry.Kind == "argument"
                    && entry.Actor == f.A.Id && memory.Participants.Count == 2
                    && Contains(memory.Participants, f.A.Id) && Contains(memory.Participants, f.B.Id))
                && (Contains(memory.Participants, resident.Id)
                    ? memory.LearnedBy == "participant"
                    : resident.Id == f.C.Id && memory.Event == argument.Event && memory.LearnedBy == edge);
        }
        Check(count >= 9 && joined, "SEIT-REL-03-P1.three-day-every-memory-event-learning-join");

        int before = f.World.ComputeStateHash();
        MemoryEntry explanation = f.Relations.Explain(f.C.Id, argument.Event);
        Check(SameMemory(learned, explanation)
            && f.Relations.Explain(f.C.Id, (EventId)999999L) == null
            && before == f.World.ComputeStateHash(),
            "SEIT-REL-05-P1.SEIT-REL-05-N1.explanation-projection-matches-event-and-learning-edge");
        IList<ResidentId> participants = explanation == null ? null : explanation.Participants as IList<ResidentId>;
        Check(explanation != null && participants != null && participants.IsReadOnly,
            "SEIT-REL-05-N1.explanation-participants-read-only");

        f.World.SetAlive(Core, f.A.Id, false);
        ResidentSummary retained;
        Check(((IResidentDirectory)f.World).TryGet(f.A.Id, out retained) && retained.Id == f.A.Id
            && SameMemory(Memory(f.B, argument.Event), f.Relations.Explain(f.B.Id, argument.Event)),
            "SEIT-IF-RESOLVE-N10.deceased-identity-and-event-retained-local");
    }

    private static double SocialFactor(Fixture f)
    {
        CommandArbitrator arbitrator = new CommandArbitrator(f.World, new Clock(f.World));
        Autonomy autonomy = new Autonomy(f.World, arbitrator);
        AutonomyDecision decision = autonomy.Decide(f.B.Id,
            new List<AvailableAction> { new AvailableAction(new ActivityId("ACT-REL-01"), (long)f.A.Id) }.AsReadOnly(), 1.0);
        return decision.Candidates.Count == 1 ? decision.Candidates[0].SocialFactor : double.NaN;
    }

    private static void ExpiryAndReload()
    {
        Fixture f = new Fixture();
        long start = 4L * Day + 8L * 3600L; // Day 5, 08:00.
        f.World.SetGameSeconds(Core, start);
        RelationshipResult result = f.Complete("ACT-REL-04");
        Feeling hurt = FindFeeling(f.B, FeelingKind.Hurt, result.Event);
        long expiry = start + 4L * Day;
        // Remove bond contribution in the fixture; oracle isolates Hurt's 0.5.
        f.World.SetBond(Core, f.B.Id, f.A.Id, 0.0f, 0.0f);
        f.World.SetGameSeconds(Core, start + Day);
        f.Relations.Tick(Core);
        Check(hurt != null && hurt.ExpiryGameSeconds == expiry && Near(SocialFactor(f), 0.5),
            "SEIT-REL-04-P1.hurt-two-four-day-duration-and-half-social-factor");
        Clock clock = new Clock(f.World);
        clock.SetSpeed(SimulationSpeed.Paused);
        long paused = f.World.GameSeconds;
        clock.AdvanceRealSeconds(Core, 120.0);
        f.Relations.Tick(Core);
        Check(f.World.GameSeconds == paused && FindFeeling(f.B, FeelingKind.Hurt, result.Event) != null,
            "SEIT-REL-04-N1.paused-two-minutes-never-consume-feeling-time-local");

        f.World = WorldState.LoadFromSnapshot(f.World.ExportSnapshot());
        f.Relations = new Relationships(f.World);
        f.World.TryGetResident(f.A.Id, out f.A);
        f.World.TryGetResident(f.B.Id, out f.B);
        f.Relations.Tick(Core);
        hurt = FindFeeling(f.B, FeelingKind.Hurt, result.Event);
        Check(f.World.GameSeconds == paused && hurt != null && hurt.ExpiryGameSeconds == expiry,
            "SEIT-REL-04-N1.snapshot-reload-preserves-expiry-no-catchup-local");
        f.World.SetGameSeconds(Core, expiry - 6L);
        f.Relations.Tick(Core);
        Check(FindFeeling(f.B, FeelingKind.Hurt, result.Event) != null && Near(SocialFactor(f), 0.5),
            "SEIT-REL-04-P1.last-tick-before-expiry-active");
        f.World.SetGameSeconds(Core, expiry);
        f.Relations.Tick(Core);
        Check(FindFeeling(f.B, FeelingKind.Hurt, result.Event) == null && Near(SocialFactor(f), 1.0)
            && Memory(f.B, result.Event) != null && Event(f.World, result.Event) != null,
            "SEIT-REL-04-P1.exact-expiry-removes-feeling-keeps-history-restores-scoring");
        f.World.SetGameSeconds(Core, expiry + 6L);
        f.Relations.Tick(Core);
        Check(Near(SocialFactor(f), 1.0), "SEIT-REL-04-P1.after-expiry-baseline");
    }

    private static void InvalidInputsAndPersistence()
    {
        Fixture f = new Fixture();
        RefusedUnchanged(f, () => f.Complete("ACT-NOPE-00"),
            "SEIT-REL-01-N1.unknown-activity-refused-with-reason-no-state-change");
        RefusedUnchanged(f, () => f.Relations.Complete(Core, new ActivityId("ACT-REL-04"), f.A.Id, (ResidentId)999999L),
            "SEIT-REL-02-N1.unknown-participant-refused-no-state-change");
        foreach (double draw in new[] { -0.01, 1.0, double.NaN, double.PositiveInfinity })
        {
            Fixture invalid = new Fixture();
            RefusedUnchanged(invalid, () => invalid.Complete("ACT-REL-04", 0.0, draw),
                "SEIT-REL-02-N1.invalid-loser-draw-" + draw.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        f = new Fixture();
        RelationshipResult result = f.Complete("ACT-REL-04");
        MemoryEntry memory = Memory(f.B, result.Event);
        f.World.AddFamilyLink(Core, f.B.Id, f.C.Id, "sibling");
        PairBond bond = Bond(f.B, f.A.Id).Clone();
        HomeRecord home = f.World.AllocateHome(Core, 4);
        HouseholdRecord household = f.World.AllocateHousehold(Core, "Moved", home.Id);
        f.World.SetHousehold(Core, f.B.Id, household.Id, home.Id);
        WorldState loaded = WorldState.LoadFromSnapshot(f.World.ExportSnapshot());
        ResidentRecord after;
        loaded.TryGetResident(f.B.Id, out after);
        Check(after != null && after.Home == home.Id && after.Id == f.B.Id
            && Bond(after, f.A.Id).Friendship == bond.Friendship && Bond(after, f.A.Id).Romance == bond.Romance
            && after.Family.Count == 1 && after.Family[0].Other == f.C.Id && after.Family[0].Kind == "sibling"
            && SameMemory(memory, Memory(after, result.Event))
            && FindFeeling(after, FeelingKind.Hurt, result.Event) != null,
            "SEIT-AC-06.move-snapshot-reload-retains-bonds-family-feeling-and-memory-local");
    }

    private static void Determinism()
    {
        Fixture a = new Fixture(1234L);
        Fixture b = new Fixture(1234L);
        bool same = true;
        for (int i = 0; i < 5; i++)
        {
            RelationshipResult left = a.Complete("ACT-REL-04", null, null, null, null);
            RelationshipResult right = b.Complete("ACT-REL-04", null, null, null, null);
            same &= left.Applied && right.Applied && left.Event == right.Event
                && left.Draws.Count > 0 && left.Draws.Count == right.Draws.Count;
            for (int j = 0; j < Math.Min(left.Draws.Count, right.Draws.Count); j++)
            {
                SocialDraw x = left.Draws[j];
                SocialDraw y = right.Draws[j];
                same &= x.Kind == y.Kind && x.Resident == y.Resident && x.Other == y.Other
                    && x.Draw == y.Draw && x.Probability == y.Probability && x.Accepted == y.Accepted;
            }
            same &= a.World.ComputeStateHash() == b.World.ComputeStateHash();
        }
        Check(same, "SEIT-REL-02-N1.seed-1234-repeated-world-entry-identical-draws-and-state");
        bool mutable = false;
        foreach (Type type in new[] { typeof(Relationships), typeof(RelationshipResult) })
        foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            mutable |= !field.IsInitOnly && !field.IsLiteral;
        Check(!mutable, "SEIT-REL-02-N1.fast-enter-play-no-static-mutable-product-state");
    }

    public static int Main()
    {
        failures = 0;
        SeparateKinds();
        IndividualFeelings();
        FactualMemories();
        ExpiryAndReload();
        InvalidInputsAndPersistence();
        Determinism();
        failures += M1Regressions.Run("RELATION");
        Console.WriteLine(failures == 0 ? "RELATION ALL PASS" : "RELATION FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
