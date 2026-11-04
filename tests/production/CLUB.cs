/* CLUB@1 red contract, BP-ACT-10 / BP-SEC-05 catalog.
 * New product facade: Blockparty.Activities.Club in Activities/Club.cs,
 * exposed as GameStack.Club and LoadedGame.Club by the public save boundary.
 * Required signatures (readable fields or properties are interchangeable):
 *   void SetMembership(StateWriter writer, string club, ResidentId member, bool joined)
 *   int Standing(string club, ResidentId resident)
 *   RelationshipResult ClubMeeting(StateWriter writer, string club, ResidentId[] attendees)
 *   void RegisterHelpTarget(StateWriter writer, ObjectId target, HouseholdId household, string state)
 *   string HelpTargetState(ObjectId target) // Damaged or Dirty -> Ready, one step
 *   RelationshipResult CommunityHelp(StateWriter writer, ResidentId helper,
 *       ObjectId target, ResidentId[] participants)
 *   RelationshipResult ResolveGratitude(StateWriter writer, ResidentId resident,
 *       EventId helpEvent, double? sentimentDraw = null)
 * These are post-completion boundaries, like RELATION.Complete. CommunityHelp
 * records factual participant memories; ResolveGratitude evaluates each helped
 * resident/help EventId once, only after participation or real Chat learning.
 * RelationshipResult and SocialDraw reuse RELATION/PERSONALITY, not test doubles.
 * Local slices: SEIT-ACT-10-P1/N1; SEIT-AC-04 club consequences only. UI,
 * travel/stages, weekly scheduling and whole-case acceptance remain at their
 * recorded gates. A package module is required by run-standalone's convention.
 * Resource group: isolated-csharp; no editor, display, port or external service.
 * negative-fixture contaminates completed help with a real meeting's standing
 * gain; the unchanged no-standing assertion must print FAIL and return 1.
 */
using System;
using System.IO;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;

public static class ClubChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private const string Garden = "GardenClub";
    private static int failures;

    private sealed class EmptyScene : ISceneContent
    {
        public void RegisterObjects(ActivityExecutor executor) { }
    }

    private sealed class Fixture
    {
        public readonly GameStack Stack;
        public readonly ResidentRecord[] Residents = new ResidentRecord[7];
        public readonly ObjectId Target = (ObjectId)401L;
        public WorldState World { get { return Stack.World; } }
        public Club Club { get { return Stack.Club; } }
        public ResidentId Id(int number) { return Residents[number - 1].Id; }

        public Fixture(long seed = 42L, string state = "Damaged")
        {
            WorldState world = WorldState.CreateSeeded(7L, seed, 0, null);
            HomeRecord home = world.AllocateHome(Core, 8);
            HouseholdRecord household = world.AllocateHousehold(Core, "Helped", home.Id);
            HomeRecord helperHome = world.AllocateHome(Core, 2);
            HouseholdRecord helperHousehold = world.AllocateHousehold(Core, "Helper", helperHome.Id);
            for (int i = 0; i < Residents.Length; i++)
            {
                bool helper = i == 3;
                Residents[i] = world.AllocateResident(Core, "R" + (i + 1), LifeStage.Adult,
                    helper ? helperHousehold.Id : household.Id, helper ? helperHome.Id : home.Id);
                world.SetNeed(Core, Residents[i].Id, Need.Social, 40.0f);
                world.SetTrait(Core, Residents[i].Id, TraitAxis.Sociable, 0);
            }
            Clock clock = new Clock(world);
            CommandArbitrator arbitrator = new CommandArbitrator(world, clock);
            Stack = new GameStack(world, clock, arbitrator,
                new ActivityExecutor(world, arbitrator, clock), new AppearanceStore());
            for (int i = 1; i <= 4; i++) Club.SetMembership(Core, Garden, Id(i), true);
            Club.RegisterHelpTarget(Core, Target, household.Id, state);
            World.SetGameSeconds(Core, 5400L);
        }
    }

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    private static void Run(string name, Action test)
    {
        try { test(); }
        catch (Exception e) { Check(false, name + " " + e); }
    }

    private static ResidentRecord Resident(WorldState world, ResidentId id)
    {
        ResidentRecord result;
        if (!world.TryGetResident(id, out result)) throw new Exception("Fixture resident missing");
        return result;
    }

    private static float Friendship(Fixture f, int from, int toward)
    {
        foreach (PairBond bond in Resident(f.World, f.Id(from)).Bonds)
            if (bond.Other == f.Id(toward)) return bond.Friendship;
        return 0.0f;
    }

    private static int Grateful(WorldState world, ResidentId resident, ResidentId helper, EventId source)
    {
        int count = 0;
        foreach (Feeling feeling in Resident(world, resident).Feelings)
            if (feeling.Kind == FeelingKind.Grateful && feeling.Other == helper
                && feeling.SourceEvent == source && feeling.Intensity == 1) count++;
        return count;
    }

    private static void Meetings()
    {
        Fixture f = new Fixture();
        f.World.SetBond(Core, f.Id(1), f.Id(2), 10.0f, 0.0f);
        f.World.SetBond(Core, f.Id(2), f.Id(1), 20.0f, 0.0f);
        for (int attendance = 1; attendance <= 2; attendance++)
        {
            f.World.SetGameSeconds(Core, attendance * 7L * 86400L + 5400L);
            RelationshipResult meeting = f.Club.ClubMeeting(Core, Garden, new[] { f.Id(1), f.Id(2) });
            Check(meeting.Applied && f.Club.Standing(Garden, f.Id(1)) == attendance
                && f.Club.Standing(Garden, f.Id(2)) == attendance,
                "SEIT-ACT-10-P1-meeting-standing-plus-one-attendance-" + attendance);
            Check(Resident(f.World, f.Id(1)).GetNeed(Need.Social) == 40.0f + 20.0f * attendance
                && Resident(f.World, f.Id(2)).GetNeed(Need.Social) == 40.0f + 20.0f * attendance
                && Friendship(f, 1, 2) == 10.0f + 3.0f * attendance
                && Friendship(f, 2, 1) == 20.0f + 3.0f * attendance,
                "SEIT-ACT-10-P1-meeting-social-and-pairwise-friendship-" + attendance);
        }
        Check(f.Club.Standing(Garden, f.Id(3)) == 0 && Friendship(f, 1, 3) == 0.0f
            && Friendship(f, 3, 1) == 0.0f && Friendship(f, 2, 3) == 0.0f
            && Friendship(f, 3, 2) == 0.0f
            && Resident(f.World, f.Id(3)).GetNeed(Need.Social) == 40.0f,
            "SEIT-ACT-10-N1-absent-member-no-standing-social-or-coattendee-gain");
        Check(f.Club.Standing("BookClub", f.Id(1)) == 0,
            "SEIT-ACT-10-P1-meeting-standing-belongs-to-attended-club");
    }

    private static RelationshipResult Help(Fixture f)
    {
        return f.Club.CommunityHelp(Core, f.Id(4), f.Target, new[] { f.Id(4), f.Id(5) });
    }

    private static void HelpEffects(bool negative)
    {
        foreach (string state in new[] { "Damaged", "Dirty" })
        {
            Fixture f = new Fixture(42L, state);
            RelationshipResult help = Help(f);
            Check(help.Applied && f.Club.HelpTargetState(f.Target) == "Ready",
                "SEIT-ACT-10-P1-help-" + state + "-one-state-step");
            if (negative) f.Club.ClubMeeting(Core, Garden, new[] { f.Id(4) });
            bool unchanged = true;
            for (int i = 1; i <= 7; i++) unchanged &= f.Club.Standing(Garden, f.Id(i)) == 0;
            Check(unchanged, "SEIT-ACT-10-P1-community-help-no-club-standing-" + state);
        }
    }

    private static EventId Chat(Fixture f, int speaker, int listener)
    {
        f.World.SetGameSeconds(Core, f.World.GameSeconds + 900L);
        RelationshipResult chat = new Relationships(f.World).Complete(Core,
            new ActivityId("ACT-REL-01"), f.Id(speaker), f.Id(listener));
        if (!chat.Applied) throw new Exception("Real Chat failed: " + chat.Reason);
        return chat.Event;
    }

    private static void Gratitude()
    {
        Fixture f = new Fixture();
        RelationshipResult help = Help(f);
        Check(help.Applied, "SEIT-ACT-10-P1-factual-help-recorded");
        RelationshipResult participant = f.Club.ResolveGratitude(Core, f.Id(5), help.Event, 0.0);
        Check(participant.Applied && Grateful(f.World, f.Id(5), f.Id(4), help.Event) == 1,
            "SEIT-ACT-10-P1-help-participant-grateful-one-toward-actual-helper");
        MemoryEntry memory = new Relationships(f.World).Explain(f.Id(5), help.Event);
        Check(memory != null && memory.LearnedBy == "participant",
            "SEIT-ACT-10-P1-help-participation-memory-links-actual-event");

        f.Club.ResolveGratitude(Core, f.Id(6), help.Event, 0.0);
        Check(Resident(f.World, f.Id(6)).Feelings.Count == 0,
            "SEIT-ACT-10-N1-helped-but-not-yet-informed-no-grateful");
        EventId chat = Chat(f, 5, 6);
        memory = new Relationships(f.World).Explain(f.Id(6), help.Event);
        bool learned = memory != null && memory.LearnedBy == "told-by:" + ((long)chat).ToString();
        bool participantLeak = false;
        if (memory != null)
            foreach (ResidentId id in memory.Participants) participantLeak |= id == f.Id(6);
        RelationshipResult informed = f.Club.ResolveGratitude(Core, f.Id(6), help.Event, 0.0);
        Check(learned && !participantLeak && informed.Applied
            && Grateful(f.World, f.Id(6), f.Id(4), help.Event) == 1,
            "SEIT-ACT-10-P1-real-chat-informed-gratitude-retains-learning-edge");
        Chat(f, 1, 7); // A real Chat with an uninformed speaker conveys no help knowledge.
        f.Club.ResolveGratitude(Core, f.Id(7), help.Event, 0.0);
        Check(Resident(f.World, f.Id(7)).Feelings.Count == 0
            && new Relationships(f.World).Explain(f.Id(7), help.Event) == null,
            "SEIT-ACT-10-N1-uninformed-resident-no-grateful-even-after-unrelated-chat");
        f.Club.ResolveGratitude(Core, f.Id(5), help.Event, 0.0);
        f.Club.ResolveGratitude(Core, f.Id(6), help.Event, 0.0);
        Check(Grateful(f.World, f.Id(5), f.Id(4), help.Event) == 1
            && Grateful(f.World, f.Id(6), f.Id(4), help.Event) == 1,
            "SEIT-ACT-10-P1-gratitude-once-per-resident-and-help-event");

        Fixture missed = new Fixture();
        RelationshipResult otherHelp = Help(missed);
        RelationshipResult draw = missed.Club.ResolveGratitude(Core, missed.Id(5), otherHelp.Event, 0.999999);
        Check(draw.Applied && draw.Draws.Count == 1 && !draw.Draws[0].Accepted
            && draw.Draws[0].Resident == missed.Id(5)
            && draw.Draws[0].Draw == 0.999999 && Resident(missed.World, missed.Id(5)).Feelings.Count == 0,
            "SEIT-ACT-10-P1-eligible-participant-separate-draw-may-yield-no-feeling");
        missed.Club.ResolveGratitude(Core, missed.Id(5), otherHelp.Event, 0.0);
        Check(Resident(missed.World, missed.Id(5)).Feelings.Count == 0,
            "SEIT-ACT-10-P1-missed-sentiment-not-rerolled");
    }

    private static void RoundTrip()
    {
        Fixture f = new Fixture();
        f.Club.ClubMeeting(Core, Garden, new[] { f.Id(1), f.Id(2) });
        RelationshipResult help = Help(f);
        f.Club.ResolveGratitude(Core, f.Id(5), help.Event, 0.999999);
        string directory = Path.Combine(Path.GetTempPath(), "blockparty-club-" + Guid.NewGuid().ToString("N"));
        try
        {
            SaveWriteResult saved = SaveV1.SaveGame(f.Stack, directory);
            if (!saved.Ok) throw new Exception("Public save failed: " + saved.Detail);
            SaveLoadResult load = SaveV1.LoadGame(directory, new EmptyScene());
            if (!load.Ok || load.Game == null) throw new Exception("Public load failed: " + load.Notice);
            LoadedGame game = load.Game;
            Check(game.Club.Standing(Garden, f.Id(1)) == 1
                && game.Club.Standing(Garden, f.Id(2)) == 1
                && game.Club.Standing(Garden, f.Id(3)) == 0
                && game.Club.Standing(Garden, f.Id(4)) == 0
                && game.Club.HelpTargetState(f.Target) == "Ready",
                "SEIT-ACT-10-P1-save-roundtrip-standing-and-help-target");
            game.Club.ClubMeeting(Core, Garden, new[] { f.Id(1), f.Id(2) });
            Check(game.Club.Standing(Garden, f.Id(1)) == 2
                && game.Club.Standing(Garden, f.Id(2)) == 2,
                "SEIT-ACT-10-P1-save-roundtrip-membership-supports-next-attendance");
            game.Club.ResolveGratitude(Core, f.Id(5), help.Event, 0.0);
            Check(Resident(game.World, f.Id(5)).Feelings.Count == 0,
                "SEIT-ACT-10-P1-save-roundtrip-no-sentiment-reroll");
            game.World.SetGameSeconds(Core, game.World.GameSeconds + 900L);
            new Relationships(game.World).Complete(Core, new ActivityId("ACT-REL-01"), f.Id(5), f.Id(6));
            RelationshipResult learned = game.Club.ResolveGratitude(Core, f.Id(6), help.Event, 0.0);
            Check(learned.Applied && Grateful(game.World, f.Id(6), f.Id(4), help.Event) == 1,
                "SEIT-ACT-10-P1-save-roundtrip-help-provenance-can-inform-new-recipient");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void FreshInstances()
    {
        for (int run = 0; run < 2; run++)
        {
            Fixture f = new Fixture();
            Check(f.Club.Standing(Garden, f.Id(1)) == 0 && f.Club.HelpTargetState(f.Target) == "Damaged",
                "SEIT-ACT-10-P1-fresh-world-no-static-club-state-" + run);
            f.Club.ClubMeeting(Core, Garden, new[] { f.Id(1), f.Id(2) });
            Help(f);
        }
    }

    public static int Main(string[] args)
    {
        if (Array.IndexOf(args, "negative-fixture") >= 0)
            Run("CLUB-negative-fixture", delegate { HelpEffects(true); });
        else
        {
            Run("CLUB-meetings", Meetings);
            Run("CLUB-help", delegate { HelpEffects(false); });
            Run("CLUB-gratitude", Gratitude);
            Run("CLUB-save-roundtrip", RoundTrip);
            Run("CLUB-fresh-instances", FreshInstances);
        }
        if (failures == 0) Console.WriteLine("CLUB ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
