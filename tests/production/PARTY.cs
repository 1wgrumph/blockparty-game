/* PARTY@1 TDD contract, BP-ACT-07 and catalog ACT-PARTY-01/02.
 * New product seam follows GameStack.Club: GameStack.Party / LoadedGame.Party
 * expose the SAME live Blockparty.Activities.Party bound to that stack.
 *   void Tick(StateWriter writer)
 *   void Enter(StateWriter writer, ActionInstanceId party, ResidentId attendee,
 *              double? sentimentDraw = null)
 *   void Exit(StateWriter writer, ActionInstanceId party, ResidentId attendee)
 * Host commands use the real arbitrator/NAV. Enter is the accepted invitation's
 * actual arrival boundary, not an invitation or completion. Exit records actual
 * departure. The party id is the host action id, stable to callers across load
 * (SAVE may remap its active NAV action). Tick observes real completed Chats
 * and host terminals; cancellation comes through a conflicting player command.
 * Injected sentiment draws must be stored once per attendee, including misses.
 * Reflection keeps the unfixed suite executable and names missing capability.
 *
 * Local mapping: Attendance/ChatWindow/SentimentGates -> SEIT-ACT-07-P1;
 * Cancelled/Reload -> SEIT-ACT-07-N1; once-only checks -> SEIT-IF-EXEC-P5's
 * party subclaim. All contribute PARTY slices of SEIT-AC-04/06. Full UI,
 * invitation/scheduling behavior, minigolf, packaged restart and whole-case
 * acceptance remain at their recorded gates; these are not acceptance claims.
 * Candidate numeric tuning is the SEIT oracle, not measured game balance.
 * New suite required by the package runner. Resource group: isolated-csharp;
 * real NAV/SOCIAL/Accounts/SAVE, no engine, display, ports or external I/O.
 * negative-fixture uses real Accounts transfers to corrupt the host charge
 * into 30 per attendee; the SAME HostCost assertion must fail. No fake service.
 */
using System;
using System.IO;
using System.Reflection;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;

public static class PartyChecks
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
        public GameStack Stack;
        public Social Social;
        public object Party;
        public readonly ResidentId[] Ids = new ResidentId[5];

        public Fixture()
        {
            World = WorldState.CreateSeeded(7L, 4242L, 0, null);
            HomeRecord home = World.AllocateHome(Core, 8);
            HouseholdRecord household = World.AllocateHousehold(Core, "Party", home.Id);
            for (int i = 0; i < Ids.Length; i++)
            {
                Ids[i] = World.AllocateResident(Core, "R" + (i + 1), LifeStage.Adult,
                    household.Id, home.Id).Id;
                World.SetMoney(Core, Ids[i], 100L);
                World.SetNeed(Core, Ids[i], Need.Social, 40.0f);
                World.SetNeed(Core, Ids[i], Need.Fun, 40.0f);
                World.SetNeed(Core, Ids[i], Need.Energy, 80.0f);
                World.SetTrait(Core, Ids[i], TraitAxis.Sociable, 0);
            }
            Clock = new Clock(World);
            Arbitrator = new CommandArbitrator(World, Clock);
            Executor = new ActivityExecutor(World, Arbitrator, Clock);
            Stack = new GameStack(World, Clock, Arbitrator, Executor, new AppearanceStore());
            Social = new Social(World, Arbitrator, Executor);
            foreach (ResidentId id in Ids)
                Executor.SetResidentPosition(id, new ApproachPose(0.0f, 0.0f, 0.0f));
        }

        public ResidentRecord R(int index)
        {
            ResidentRecord resident;
            Require(World.TryGetResident(Ids[index], out resident), "real fixture resident missing");
            return resident;
        }

        public void AttachParty() { Party = PartyMember(Stack); }

        public void Tick()
        {
            // SOCIAL drives the real executor; PARTY consumes its factual terminals.
            Social.Tick(Exec);
            if (Party != null) Invoke(Party, "Tick", new[] { typeof(StateWriter) }, Exec);
        }

        public void Enter(ActionInstanceId party, int index, double draw)
        {
            Console.WriteLine("DRAW seed=4242 attendee=R" + (index + 1) + " stored="
                + draw.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            Invoke(Party, "Enter", new[] { typeof(StateWriter), typeof(ActionInstanceId),
                typeof(ResidentId), typeof(double?) }, Exec, party, Ids[index], (double?)draw);
        }

        public void Exit(ActionInstanceId party, int index)
        {
            Invoke(Party, "Exit", new[] { typeof(StateWriter), typeof(ActionInstanceId),
                typeof(ResidentId) }, Exec, party, Ids[index]);
        }
    }

    private static object PartyMember(object stack)
    {
        Type type = stack.GetType();
        FieldInfo field = type.GetField("Party");
        PropertyInfo property = type.GetProperty("Party");
        object party = field != null ? field.GetValue(stack) : property != null ? property.GetValue(stack) : null;
        Require(party != null && party.GetType().FullName == "Blockparty.Activities.Party",
            "missing PARTY capability " + type.Name + ".Party (Blockparty.Activities.Party)");
        return party;
    }

    private static void Invoke(object service, string name, Type[] signature, params object[] args)
    {
        Require(service != null, "missing PARTY capability Party");
        MethodInfo method = service.GetType().GetMethod(name, signature);
        Require(method != null, "missing PARTY capability Party." + name);
        method.Invoke(service, args);
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

    private static ActionInstanceId Begin(Fixture f, int actor, string activity, long? target = null)
    {
        CommandResult result = f.Arbitrator.Submit(new CommandEnvelope(f.World.AllocateCommand(Core),
            f.World.Epoch, f.World.GameSeconds, CommandSource.PlayerHuman,
            f.R(actor).ResidentSequence + 1u, CommandKind.DirectActivity, f.Ids[actor],
            new ActivityId(activity), target, new CommandParameters.Direct(QueueMode.Immediate)));
        Require(result.Accepted, "real command refused " + activity + ": " + result.Reason);
        ActionInstanceId action = f.Arbitrator.GetCurrent(f.Ids[actor]).Action;
        f.Tick();
        f.Tick();
        Require(f.Executor.GetExecution(action).Stage == ExecutionStage.Perform,
            "real NAV fixture did not reach Perform: " + activity);
        return action;
    }

    private static void Advance(Fixture f, long seconds)
    {
        Require(seconds >= 0L && seconds % 6L == 0L, "fixture delta must be whole fixed ticks");
        for (long elapsed = 0L; elapsed < seconds; elapsed += 6L)
        {
            f.Clock.AdvanceRealSeconds(Core, 0.1);
            f.Tick();
        }
    }

    private static float Friendship(Fixture f, int from, int to)
    {
        foreach (PairBond bond in f.R(from).Bonds)
            if (bond.Other == f.Ids[to]) return bond.Friendship;
        return 0.0f;
    }

    private static bool Needs(Fixture f, int index, float social, float fun, float energy)
    {
        return f.R(index).GetNeed(Need.Social) == social && f.R(index).GetNeed(Need.Fun) == fun
            && f.R(index).GetNeed(Need.Energy) == energy;
    }

    private static void HostCost(Fixture f, string name)
    {
        bool ok = f.R(0).Money == 70L;
        for (int i = 1; i < f.Ids.Length; i++) ok &= f.R(i).Money == 100L;
        Check(ok, name + ".host-cost-30-once-not-per-attendee");
    }

    private static WorldEvent PartyEvent(Fixture f)
    {
        WorldEvent found = null;
        foreach (WorldEvent entry in f.World.Events)
            if (string.Equals(entry.Kind, "party", StringComparison.OrdinalIgnoreCase))
            {
                Require(found == null, "duplicate factual party event");
                found = entry;
            }
        Require(found != null, "missing factual party event");
        return found;
    }

    private static int Feelings(Fixture f, int index, FeelingKind kind)
    {
        int count = 0;
        foreach (Feeling feeling in f.R(index).Feelings) if (feeling.Kind == kind) count++;
        return count;
    }

    private static void ProducerQualification(bool corrupt)
    {
        Fixture f = new Fixture();
        ActionInstanceId host = Begin(f, 0, "ACT-PARTY-01");
        Advance(f, 10800L);
        Check(f.Executor.GetExecution(host).Outcome == TerminalOutcome.Completed,
            "PARTY.producer-NAV-host-natural-terminal");
        ActionInstanceId chat = Begin(f, 1, "ACT-REL-01", (long)f.Ids[2]);
        Advance(f, 900L);
        Check(f.Executor.GetExecution(chat).Outcome == TerminalOutcome.Completed
            && Friendship(f, 1, 2) == 3.0f && Friendship(f, 2, 1) == 3.0f
            && Needs(f, 1, 50.0f, 40.0f, 80.0f) && Needs(f, 2, 50.0f, 40.0f, 80.0f),
            "PARTY.producer-real-completed-chat-own-plus3-plus10");
        Accounts accounts = new Accounts(f.World);
        for (int charge = 0; charge < (corrupt ? 3 : 1); charge++)
            Require(accounts.Transfer(Exec, f.World.AllocateAction(Core),
                AccountRef.Personal(f.Ids[0]), AccountRef.Expense("PartyFoodAndDrink"), 30L).Accepted,
                "real account fixture refused party expense");
        if (corrupt) Console.WriteLine("PARTY negative-fixture: real transfers charged host 30 for each of 3 attendees");
        HostCost(f, "PARTY.producer");
        Reload(f, false);
        // Epoch is intentionally incremented on load; check producer observations.
        Check(Friendship(f, 1, 2) == 3.0f && Friendship(f, 2, 1) == 3.0f
            && Needs(f, 1, 50.0f, 40.0f, 80.0f) && f.R(0).Money == (corrupt ? 10L : 70L),
            "PARTY.producer-public-save-roundtrip");
    }

    private static void Attendance()
    {
        Fixture f = new Fixture();
        f.AttachParty();
        ActionInstanceId party = Begin(f, 0, "ACT-PARTY-01");
        HostCost(f, "PARTY.entry");
        f.Enter(party, 1, 0.0);
        f.Enter(party, 2, 0.999999);
        f.Enter(party, 3, 0.0);
        ActionInstanceId chat = Begin(f, 1, "ACT-REL-01", (long)f.Ids[2]);
        Advance(f, 900L);
        Require(f.Executor.GetExecution(chat).Outcome == TerminalOutcome.Completed,
            "real party Chat did not complete");
        Check(Friendship(f, 1, 2) == 3.0f && Friendship(f, 2, 1) == 3.0f,
            "PARTY.chat-own-effects-separate-before-party-commit");
        Advance(f, 4500L);
        f.Exit(party, 3);
        Check(Needs(f, 3, 55.0f, 50.0f, 72.5f), "PARTY.half-90-minute-proportional-effects");
        int exited = f.World.ComputeStateHash();
        f.Exit(party, 3);
        f.Tick();
        Check(f.World.ComputeStateHash() == exited, "PARTY.repeated-exit-once");
        Advance(f, 5400L);
        Check(f.Executor.GetExecution(party).Outcome == TerminalOutcome.Completed
            && Needs(f, 1, 80.0f, 60.0f, 65.0f) && Needs(f, 2, 80.0f, 60.0f, 65.0f)
            && Needs(f, 3, 55.0f, 50.0f, 72.5f) && Needs(f, 4, 40.0f, 40.0f, 80.0f),
            "PARTY.full-180-minute-effects-and-nonattendee-unchanged");
        Check(Friendship(f, 1, 2) == 7.0f && Friendship(f, 2, 1) == 7.0f
            && Friendship(f, 1, 3) == 0.0f && Friendship(f, 3, 1) == 0.0f
            && Friendship(f, 2, 3) == 0.0f && Friendship(f, 3, 2) == 0.0f,
            "PARTY.completed-chat-pair-only-plus4-on-top-of-chat-plus3");
        HostCost(f, "PARTY.end");
        WorldEvent factual = PartyEvent(f);
        Feeling grateful = null;
        foreach (Feeling feeling in f.R(1).Feelings)
            if (feeling.Kind == FeelingKind.Grateful) grateful = feeling;
        Check(Feelings(f, 1, FeelingKind.Grateful) == 1 && grateful != null
            && grateful.Other == f.Ids[0] && grateful.Intensity == 1
            && grateful.SourceEvent == factual.Event && grateful.ExpiryGameSeconds > factual.GameSeconds
            && Feelings(f, 2, FeelingKind.Grateful) == 0 && Feelings(f, 3, FeelingKind.Grateful) == 0
            && Feelings(f, 0, FeelingKind.Proud) == 0,
            "PARTY.individual-stored-draws-and-120-minute-chat-eligibility");
        Check(new Relationships(f.World).Explain(f.Ids[1], factual.Event) != null
            && new Relationships(f.World).Explain(f.Ids[2], factual.Event) != null
            && new Relationships(f.World).Explain(f.Ids[4], factual.Event) == null,
            "PARTY.factual-participant-memory-no-invented-nonattendee-history");
        int ended = f.World.ComputeStateHash();
        f.Exit(party, 1);
        f.Exit(party, 2);
        for (int i = 0; i < 3; i++) f.Tick();
        Check(f.World.ComputeStateHash() == ended, "PARTY.terminal-and-attendee-effects-once");
    }

    private static void Cancelled()
    {
        Fixture f = new Fixture();
        f.AttachParty();
        ActionInstanceId party = Begin(f, 0, "ACT-PARTY-01");
        f.Enter(party, 1, 0.0);
        Advance(f, 3600L);
        f.Exit(party, 1);
        Check(Needs(f, 1, 50.0f, 46.666668f, 75.0f), "PARTY.one-third-60-minute-exit");
        Begin(f, 0, "ACT-HOME-01");
        Require(f.Executor.GetExecution(party).Outcome == TerminalOutcome.Interrupted,
            "real host cancellation did not interrupt party");
        Advance(f, 7200L);
        HostCost(f, "PARTY.cancelled-no-refund");
        Check(Needs(f, 1, 50.0f, 46.666668f, 75.0f) && Needs(f, 2, 40.0f, 40.0f, 80.0f)
            && Friendship(f, 1, 2) == 0.0f && Feelings(f, 1, FeelingKind.Grateful) == 0
            && Feelings(f, 0, FeelingKind.Proud) == 0,
            "PARTY.cancelled-no-later-completion-reward-or-fictional-chat");
    }

    private static void Reload(Fixture f, bool withParty)
    {
        string directory = Path.Combine(Path.GetTempPath(), "blockparty-PARTY-"
            + System.Diagnostics.Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            SaveWriteResult saved = SaveV1.SaveGame(f.Stack, directory);
            Require(saved.Ok, "public SaveGame refused fixture: " + saved.Detail);
            SaveLoadResult loaded = SaveV1.LoadGame(directory, new EmptyScene());
            Require(loaded.Ok, "public LoadGame refused fixture: " + loaded.Notice);
            LoadedGame game = loaded.Game;
            f.World = game.World;
            f.Clock = game.Clock;
            f.Arbitrator = game.Arbitrator;
            f.Executor = game.Executor;
            f.Social = new Social(f.World, f.Arbitrator, f.Executor);
            f.Party = withParty ? PartyMember(game) : null;
            // This helper loads once; it deliberately keeps no fabricated Party snapshot.
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void ReloadDuringParty()
    {
        Fixture f = new Fixture();
        f.AttachParty();
        ActionInstanceId party = Begin(f, 0, "ACT-PARTY-01");
        f.Enter(party, 1, 0.0);
        f.Enter(party, 2, 0.999999);
        f.Enter(party, 3, 0.0);
        Begin(f, 1, "ACT-REL-01", (long)f.Ids[2]);
        Advance(f, 5400L);
        f.Exit(party, 3);
        Reload(f, true);
        Check(Needs(f, 3, 55.0f, 50.0f, 72.5f) && Friendship(f, 1, 2) == 3.0f,
            "PARTY.reload-retains-partial-exit-and-actual-chat");
        f.Exit(party, 3);
        f.Tick();
        Check(Needs(f, 3, 55.0f, 50.0f, 72.5f), "PARTY.reload-repeated-exit-not-reapplied");
        Advance(f, 5400L);
        Check(Needs(f, 1, 80.0f, 60.0f, 65.0f) && Needs(f, 2, 80.0f, 60.0f, 65.0f)
            && Needs(f, 3, 55.0f, 50.0f, 72.5f) && Friendship(f, 1, 2) == 7.0f
            && Friendship(f, 2, 1) == 7.0f && Feelings(f, 1, FeelingKind.Grateful) == 1
            && Feelings(f, 2, FeelingKind.Grateful) == 0,
            "PARTY.reload-resumes-original-duration-chat-credit-and-stored-sentiment");
        HostCost(f, "PARTY.reload");
        int ended = f.World.ComputeStateHash();
        f.Exit(party, 1);
        f.Exit(party, 2);
        f.Tick();
        Check(f.World.ComputeStateHash() == ended, "PARTY.reload-terminal-replay-once");
    }

    private static void SentimentGates()
    {
        Fixture f = new Fixture();
        f.AttachParty();
        ActionInstanceId party = Begin(f, 0, "ACT-PARTY-01");
        for (int i = 1; i <= 3; i++) f.Enter(party, i, 0.0);
        Begin(f, 1, "ACT-REL-01", (long)f.Ids[2]);
        Advance(f, 900L);
        Begin(f, 2, "ACT-REL-01", (long)f.Ids[3]);
        Advance(f, 6300L);
        for (int i = 1; i <= 3; i++) f.Exit(party, i);
        Advance(f, 3600L);
        Check(Feelings(f, 0, FeelingKind.Proud) == 1
            && Feelings(f, 1, FeelingKind.Grateful) == 1 && Feelings(f, 2, FeelingKind.Grateful) == 1
            && Feelings(f, 3, FeelingKind.Grateful) == 1,
            "PARTY.three-120-minute-chat-attendees-host-proud-gate");
        HostCost(f, "PARTY.three-attendees");
    }

    public static int Main(string[] args)
    {
        if (Array.IndexOf(args, "negative-fixture") >= 0)
            Run("PARTY.negative-fixture", () => ProducerQualification(true));
        else
        {
            Run("PARTY.producer-qualification", () => ProducerQualification(false));
            Run("PARTY.attendance-chat-sentiment-once", Attendance);
            Run("PARTY.cancelled", Cancelled);
            Run("PARTY.reload-during-party", ReloadDuringParty);
            Run("PARTY.sentiment-gates", SentimentGates);
        }
        if (failures == 0) Console.WriteLine("PARTY ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
