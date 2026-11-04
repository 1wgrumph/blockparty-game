/* SOCIAL@1 TDD contract: Blockparty.Activities.Social in Activities/Social.cs:
 *   Social(WorldState world, CommandArbitrator arbitrator, ActivityExecutor executor)
 *   void Tick(StateWriter writer)
 * Commands enter the real arbitrator. Tick drives NAV and applies RELATION
 * outcomes only after natural completion, once per action. Do not also tick NAV.
 * Reflection keeps the suite executable before Social exists, so a missing
 * capability prints FAIL while real predecessor fixtures can still be qualified.
 * Oracles: BP-ACT-06, BP-REL-01..04; catalog ACT-REL-01/03/04 and
 * SEIT-ACT-06-P1/N1. Numeric catalog values remain unmeasured candidates.
 * AC-04/06 coverage is local only: no UI, party, move or packaged acceptance.
 * A new module is required by the package runner convention. Resource group:
 * isolated-csharp; real NAV/RELATION/SAVE codec, no editor, network or ports.
 * Seed 4 produces recipient-loses/Hurt, and rejected romance/initiator-only
 * Embarrassed; seed 42 accepts romance. ProducerQualification logs real draws.
 * negative-fixture copies the real loser's Hurt to the winner and feeds that
 * corrupt world to the SAME individual-feeling assertion. It must exit 1.
 */
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;

public static class SocialChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private const StateWriter Exec = StateWriter.ActivityExecutor;
    private static int failures;

    private sealed class EmptyScene : ISceneContent
    {
        public void RegisterObjects(ActivityExecutor executor) { }
    }

    private sealed class Fixture
    {
        public WorldState World;
        public Clock Clock;
        public CommandArbitrator Arbitrator;
        public ActivityExecutor Executor;
        public ResidentRecord A, B, C;
        public object Social;
        public MethodInfo TickMethod;

        public Fixture(long seed = 4L)
        {
            World = WorldState.CreateSeeded(7L, seed, 0, null);
            HomeRecord home = World.AllocateHome(Core, 8);
            HouseholdRecord household = World.AllocateHousehold(Core, "Social fixture", home.Id);
            A = World.AllocateResident(Core, "Initiator", LifeStage.Adult, household.Id, home.Id);
            B = World.AllocateResident(Core, "Recipient", LifeStage.Adult, household.Id, home.Id);
            C = World.AllocateResident(Core, "Bystander", LifeStage.Adult, household.Id, home.Id);
            foreach (ResidentRecord resident in new[] { A, B, C })
            {
                World.SetNeed(Core, resident.Id, Need.Social, 50.0f);
                World.SetNeed(Core, resident.Id, Need.Comfort, 50.0f);
                World.SetTrait(Core, resident.Id, TraitAxis.Sociable, 0);
            }
            World.SetBond(Core, A.Id, B.Id, 10.0f, 25.0f);
            World.SetBond(Core, B.Id, A.Id, 30.0f, 15.0f);
            World.SetBond(Core, A.Id, C.Id, 14.0f, 8.0f);
            World.SetBond(Core, C.Id, A.Id, 17.0f, 9.0f);
            World.AddFamilyLink(Core, A.Id, C.Id, "sibling");
            Clock = new Clock(World);
            Arbitrator = new CommandArbitrator(World, Clock);
            Executor = new ActivityExecutor(World, Arbitrator, Clock);
            foreach (ResidentRecord resident in new[] { A, B, C })
                Executor.SetResidentPosition(resident.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
        }

        public void AttachSocial()
        {
            Type type = typeof(WorldState).Assembly.GetType("Blockparty.Activities.Social");
            Require(type != null, "missing SOCIAL capability Blockparty.Activities.Social");
            Social = Activator.CreateInstance(type, new object[] { World, Arbitrator, Executor });
            TickMethod = type.GetMethod("Tick", new[] { typeof(StateWriter) });
            Require(TickMethod != null, "missing SOCIAL capability Social.Tick(StateWriter)");
        }

        public void Tick()
        {
            if (Social == null) Executor.Tick(Exec);
            else TickMethod.Invoke(Social, new object[] { Exec });
        }
    }

    private static void Require(bool ok, string detail)
    {
        if (!ok) throw new Exception(detail);
    }

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    private static void Run(string name, Action test)
    {
        try { test(); }
        catch (Exception e)
        {
            if (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            Check(false, name + " " + e.Message);
        }
    }

    private static CommandResult Submit(Fixture f, string code, long? target)
    {
        return f.Arbitrator.Submit(new CommandEnvelope(f.World.AllocateCommand(Core),
            f.World.Epoch, f.World.GameSeconds, CommandSource.PlayerHuman,
            f.A.ResidentSequence + 1u, CommandKind.DirectActivity, f.A.Id,
            new ActivityId(code), target, new CommandParameters.Direct(QueueMode.Immediate)));
    }

    private static ActionInstanceId Begin(Fixture f, string code)
    {
        CommandResult submitted = Submit(f, code, (long)f.B.Id);
        Require(submitted.Accepted, "real command fixture refused " + code + ": " + submitted.Reason);
        ActionInstanceId action = f.Arbitrator.GetCurrent(f.A.Id).Action;
        f.Tick();
        f.Tick();
        Require(f.Executor.GetExecution(action).Stage == ExecutionStage.Perform,
            "real NAV fixture did not reach Perform: " + code);
        return action;
    }

    private static void Advance(Fixture f, int seconds)
    {
        for (int elapsed = 0; elapsed < seconds; elapsed += 6)
        {
            f.Clock.AdvanceRealSeconds(Core, 0.1);
            f.Tick();
        }
    }

    private static PairBond Bond(ResidentRecord resident, ResidentId other)
    {
        foreach (PairBond bond in resident.Bonds)
            if (bond.Other == other) return bond;
        throw new Exception("fixture bond missing");
    }

    private static WorldEvent OnlyEvent(Fixture f, string kind)
    {
        WorldEvent found = null;
        foreach (WorldEvent entry in f.World.Events)
        {
            if (entry.Kind != kind) continue;
            Require(found == null, "duplicate factual " + kind + " event");
            found = entry;
        }
        Require(found != null, "missing factual " + kind + " event");
        return found;
    }

    private static bool Memory(ResidentRecord resident, WorldEvent factual, ResidentId a, ResidentId b)
    {
        foreach (MemoryEntry memory in resident.Memories)
        {
            if (memory.Event != factual.Event) continue;
            return memory.Kind == factual.Kind && memory.GameSeconds == factual.GameSeconds
                && memory.LearnedBy == "participant" && memory.Participants.Count == 2
                && ((memory.Participants[0] == a && memory.Participants[1] == b)
                    || (memory.Participants[0] == b && memory.Participants[1] == a));
        }
        return false;
    }

    private static void CheckArgument(Fixture f, string name)
    {
        WorldEvent factual = OnlyEvent(f, "argument");
        Check(Bond(f.A, f.B.Id).Friendship == 2.0f && Bond(f.B, f.A.Id).Friendship == 22.0f
            && Bond(f.A, f.B.Id).Romance == 21.0f && Bond(f.B, f.A.Id).Romance == 11.0f,
            name + ".pair-deltas");
        Feeling hurt = f.B.Feelings.Count == 1 ? f.B.Feelings[0] : null;
        Check(factual.Actor == f.A.Id && factual.Detail == "ACT-REL-04"
            && f.A.Feelings.Count == 0 && hurt != null && hurt.Kind == FeelingKind.Hurt
            && hurt.Intensity == 2 && hurt.SourceEvent == factual.Event && hurt.Other == f.A.Id
            && hurt.ExpiryGameSeconds > factual.GameSeconds
            && Memory(f.A, factual, f.A.Id, f.B.Id) && Memory(f.B, factual, f.A.Id, f.B.Id)
            && f.C.Feelings.Count == 0 && f.C.Memories.Count == 0,
            name + ".individual-factual-feelings");
    }

    private static void LogDraws(string code, RelationshipResult result)
    {
        foreach (SocialDraw draw in result.Draws)
            Console.WriteLine("DRAW seed=4 activity=" + code + " kind=" + draw.Kind
                + " resident=" + (long)draw.Resident + " probability="
                + draw.Probability.ToString("R", CultureInfo.InvariantCulture) + " value="
                + draw.Draw.ToString("R", CultureInfo.InvariantCulture));
    }

    private static void ProducerQualification()
    {
        Fixture f = new Fixture();
        RelationshipResult result = new Relationships(f.World).Complete(Exec,
            new ActivityId("ACT-REL-04"), f.A.Id, f.B.Id);
        Require(result.Applied, "real RELATION producer refused argument");
        LogDraws("ACT-REL-04", result);
        CheckArgument(f, "SOCIAL.producer-argument");
        SaveRoundTrip(f, "SOCIAL.producer-save-roundtrip");
        f = new Fixture();
        result = new Relationships(f.World).Complete(Exec, new ActivityId("ACT-REL-03"), f.A.Id, f.B.Id);
        LogDraws("ACT-REL-03", result);
        Check(result.Applied && f.A.Feelings.Count == 1 && f.B.Feelings.Count == 0
            && f.A.Feelings[0].Kind == FeelingKind.Embarrassed,
            "SOCIAL.producer-rejection-seed");
        CheckRomance(f, false, "SOCIAL.producer-rejection");
        f = new Fixture(42L);
        result = new Relationships(f.World).Complete(Exec, new ActivityId("ACT-REL-03"), f.A.Id, f.B.Id);
        Require(result.Applied, "real RELATION producer refused romance");
        CheckRomance(f, true, "SOCIAL.producer-accepted-romance");
        f = new Fixture();
        ActionInstanceId action = Begin(f, "ACT-REL-01");
        Advance(f, 900);
        Check(f.Executor.GetExecution(action).Outcome == TerminalOutcome.Completed,
            "SOCIAL.producer-NAV-natural-chat");
    }

    private static void Friendly()
    {
        Fixture f = new Fixture();
        f.AttachSocial();
        string bystander = ResidentState(f.C);
        ActionInstanceId action = Begin(f, "ACT-REL-01");
        Advance(f, 900);
        Check(f.Executor.GetExecution(action).Outcome == TerminalOutcome.Completed
            && Bond(f.A, f.B.Id).Friendship == 13.0f && Bond(f.B, f.A.Id).Friendship == 33.0f
            && Bond(f.A, f.B.Id).Romance == 25.0f && Bond(f.B, f.A.Id).Romance == 15.0f
            && f.A.GetNeed(Need.Social) == 60.0f && f.B.GetNeed(Need.Social) == 60.0f
            && Bond(f.A, f.C.Id).Friendship == 14.0f && Bond(f.A, f.C.Id).Romance == 8.0f
            && f.A.Family.Count == 1 && f.A.Family[0].Other == f.C.Id
            && f.A.Family[0].Kind == "sibling" && ResidentState(f.C) == bystander,
            "SOCIAL.friendly-pair-separation");
        OnlyEvent(f, "chat");
        string after = SocialState(f.World);
        for (int i = 0; i < 3; i++) f.Tick();
        Check(SocialState(f.World) == after, "SOCIAL.friendly-completion-once");
    }

    private static Fixture Argument()
    {
        Fixture f = new Fixture();
        f.AttachSocial();
        ActionInstanceId action = Begin(f, "ACT-REL-04");
        Advance(f, 600);
        Require(f.Executor.GetExecution(action).Outcome == TerminalOutcome.Completed,
            "argument did not complete naturally");
        CheckArgument(f, "SOCIAL.argument");
        string after = SocialState(f.World);
        for (int i = 0; i < 3; i++) f.Tick();
        Check(SocialState(f.World) == after, "SOCIAL.argument-completion-once");
        return f;
    }

    private static void AngryChat()
    {
        Fixture f = new Fixture();
        WorldEvent cause = f.World.AppendEvent(Core, "argument", f.A.Id, "fixture conflict");
        f.World.AddFeeling(Core, f.A.Id, FeelingKind.Angry, cause.Event, f.B.Id, 1, 86400L);
        f.AttachSocial();
        Begin(f, "ACT-REL-01");
        Advance(f, 900);
        Check(Bond(f.A, f.B.Id).Friendship == 8.0f && Bond(f.B, f.A.Id).Friendship == 28.0f
            && Bond(f.A, f.B.Id).Romance == 25.0f && Bond(f.B, f.A.Id).Romance == 15.0f,
            "SOCIAL.angry-chat-negative-delta");
    }

    private static void Interrupted()
    {
        Fixture f = new Fixture();
        f.AttachSocial();
        string before = SocialState(f.World);
        ActionInstanceId action = Begin(f, "ACT-REL-01");
        Advance(f, 600);
        Check(SocialState(f.World) == before, "SOCIAL.incomplete-chat-no-effects");
        Require(Submit(f, "ACT-HOME-01", null).Accepted, "interruption command refused");
        f.Tick();
        Advance(f, 300);
        Check(f.Executor.GetExecution(action).Outcome == TerminalOutcome.Interrupted
            && SocialState(f.World) == before, "SOCIAL.interrupted-chat-no-effects");
    }

    private static void Romance(bool accepted)
    {
        Fixture f = new Fixture(accepted ? 42L : 4L);
        f.AttachSocial();
        Begin(f, "ACT-REL-03");
        Advance(f, 1200);
        CheckRomance(f, accepted, accepted ? "SOCIAL.accepted-romance" : "SOCIAL.rejected-romance");
    }

    private static void CheckRomance(Fixture f, bool accepted, string name)
    {
        Check(Bond(f.A, f.B.Id).Friendship == 10.0f && Bond(f.B, f.A.Id).Friendship == 30.0f
            && Bond(f.A, f.B.Id).Romance == (accepted ? 30.0f : 23.0f)
            && Bond(f.B, f.A.Id).Romance == (accepted ? 20.0f : 13.0f)
            && f.A.Family.Count == 1 && f.A.Family[0].Other == f.C.Id,
            name + ".pair-deltas");
        WorldEvent factual = OnlyEvent(f, accepted ? "romance" : "rejection");
        if (!accepted)
            Check(f.A.Feelings.Count == 1 && f.B.Feelings.Count == 0
                && f.A.Feelings[0].Kind == FeelingKind.Embarrassed && f.A.Feelings[0].Intensity == 1
                && f.A.Feelings[0].SourceEvent == factual.Event && f.A.Feelings[0].Other == f.B.Id,
                name + ".individual-feeling");
    }

    private static string ResidentState(ResidentRecord resident)
    {
        StringBuilder text = new StringBuilder();
        text.Append((long)resident.Id).Append(':').Append(resident.GetNeed(Need.Social));
        foreach (PairBond bond in resident.Bonds)
            text.Append("|bond:").Append((long)bond.Other).Append(':').Append(bond.Friendship).Append(':').Append(bond.Romance);
        foreach (FamilyLink family in resident.Family)
            text.Append("|family:").Append((long)family.Other).Append(':').Append(family.Kind);
        foreach (Feeling feeling in resident.Feelings)
            text.Append("|feeling:").Append(feeling.Kind).Append(':').Append((long)feeling.SourceEvent)
                .Append(':').Append((long)feeling.Other).Append(':').Append(feeling.Intensity).Append(':').Append(feeling.ExpiryGameSeconds);
        foreach (MemoryEntry memory in resident.Memories)
        {
            text.Append("|memory:").Append((long)memory.Event).Append(':').Append(memory.Kind).Append(':')
                .Append(memory.GameSeconds).Append(':').Append((long)memory.Place).Append(':')
                .Append(memory.OutcomeCode).Append(':').Append(memory.LearnedBy);
            foreach (ResidentId participant in memory.Participants) text.Append(':').Append((long)participant);
        }
        return text.ToString();
    }

    private static string SocialState(WorldState world)
    {
        StringBuilder text = new StringBuilder();
        foreach (ResidentRecord resident in world.ResidentsSnapshot()) text.AppendLine(ResidentState(resident));
        foreach (WorldEvent factual in world.Events)
            text.Append((long)factual.Event).Append(':').Append(factual.GameSeconds).Append(':')
                .Append(factual.Kind).Append(':').Append(factual.Actor).Append(':').Append(factual.Detail).AppendLine();
        return text.ToString();
    }

    private static void SaveRoundTrip()
    {
        SaveRoundTrip(Argument(), "SOCIAL.save-roundtrip-facts-pairs-feelings");
    }

    private static void SaveRoundTrip(Fixture f, string name)
    {
        string expected = SocialState(f.World);
        string directory = Path.Combine(Path.GetTempPath(), "blockparty-SOCIAL-"
            + System.Diagnostics.Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            SaveWriteResult saved = SaveV1.SaveGame(new GameStack(f.World, f.Clock, f.Arbitrator,
                f.Executor, new AppearanceStore()), directory);
            Require(saved.Ok, "public SaveGame refused social fixture");
            SaveLoadResult loaded = SaveV1.LoadGame(directory, new EmptyScene());
            Require(loaded.Ok, "public LoadGame refused social fixture");
            Check(SocialState(loaded.Game.World) == expected, name);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void AcceptedWriterTick()
    {
        string expected = null;
        foreach (StateWriter writer in new[] { Exec, Core, StateWriter.CommandArbitrator })
        {
            Fixture f = new Fixture();
            f.AttachSocial();
            ActionInstanceId action = Begin(f, "ACT-REL-01");
            f.Clock.AdvanceRealSeconds(Core, 15.0,
                () => f.TickMethod.Invoke(f.Social, new object[] { writer }));
            string actual = f.World.ComputeStateHash() + "|" + SocialState(f.World);
            Check(f.Executor.GetExecution(action).Outcome == TerminalOutcome.Completed
                && Bond(f.A, f.B.Id).Friendship == 13.0f
                && Bond(f.B, f.A.Id).Friendship == 33.0f
                && (expected == null || actual == expected),
                "SOCIAL-Tick-" + writer + "-matches-executor-path");
            if (expected == null) expected = actual;
        }
    }

    public static int Main(string[] args)
    {
        if (Array.IndexOf(args, "negative-fixture") >= 0)
        {
            Run("SOCIAL.negative-fixture", () =>
            {
                Fixture f = new Fixture();
                new Relationships(f.World).Complete(Exec, new ActivityId("ACT-REL-04"), f.A.Id, f.B.Id);
                Require(f.B.Feelings.Count == 1 && f.A.Feelings.Count == 0, "real asymmetric fixture unavailable");
                Feeling hurt = f.B.Feelings[0];
                f.World.AddFeeling(Core, f.A.Id, hurt.Kind, hurt.SourceEvent, f.B.Id, hurt.Intensity, hurt.ExpiryGameSeconds);
                Console.WriteLine("SOCIAL negative-fixture: loser's factual Hurt copied to winner");
                CheckArgument(f, "SOCIAL.argument");
            });
        }
        else
        {
            Run("SOCIAL.producer-qualification", ProducerQualification);
            Run("SOCIAL.friendly-pair-separation", Friendly);
            Run("SOCIAL.argument", () => Argument());
            Run("SOCIAL.angry-chat", AngryChat);
            Run("SOCIAL.interrupted-chat", Interrupted);
            Run("SOCIAL.accepted-romance", () => Romance(true));
            Run("SOCIAL.rejected-romance", () => Romance(false));
            Run("SOCIAL.save-roundtrip", SaveRoundTrip);
            Run("social-tick-writer-contract", AcceptedWriterTick);
        }
        if (failures == 0) Console.WriteLine("SOCIAL ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
