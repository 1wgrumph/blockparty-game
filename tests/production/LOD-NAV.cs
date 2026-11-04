/* LOD-NAV test-first oracle, m4/test-engineer.json + GYM-TRAIL amendment.
 * isolated-csharp: Unity Roslyn/runtime only, no editor/display or new product API.
 * JSON receipt/geometry/P7 checks reuse the landed ENV/E1 decoder conventions in
 * production/preflight/LOD-NAV.json. The manifest and exporter schema are there.
 * NAV/COMMAND fixtures below use the landed NAV study action to isolate the
 * shared reservation primitive at every exported approach pose. They do not
 * replace the real player paths required by navmesh-paths.json.
 * negative-fixture [lod|path|approach|perf|slot|tier] corrupts observations and
 * must exit 1. --self-test [slot|tier] proves fixture rejection without future product data.
 * This local oracle does not certify the full AC-21 sustained-session gate.
 */
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Blockparty.Contracts;
using Blockparty.Simulation;

public static class LodNavChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private const StateWriter Exec = StateWriter.ActivityExecutor;
    private static int failures;
    private static string fault;
    private static void Check(bool ok, string id)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + id);
        if (!ok) failures++;
    }
    private static void Case(string id, Action action)
    {
        try { action(); }
        catch (Exception error) { Check(false, id + " " + error.GetType().Name + ": " + error.Message); }
    }
    private static void DataChecks(bool selfTest)
    {
        using (JsonDocument spec = JsonDocument.Parse(File.ReadAllText("production/preflight/LOD-NAV.json")))
        {
            string helpers = spec.RootElement.GetProperty("oracle_helpers").GetString();
            string code = helpers + "\ntry:\n run(__import__('sys').argv[1], __import__('sys').argv[2]=='true', __import__('sys').argv[3])\n print('PASS LOD-NAV data oracle')\nexcept (AssertionError,KeyError,ValueError,OSError,StopIteration) as e:\n print('FAIL LOD-NAV data oracle: '+str(e)); raise SystemExit(1)\n";
            foreach (string check in selfTest ? new[] { "negative" } : new[] { "inputs", "geometry", "paths", "performance", "visuals" })
            {
                ProcessStartInfo start = new ProcessStartInfo("python3") { UseShellExecute = false };
                start.ArgumentList.Add("-c"); start.ArgumentList.Add(code);
                start.ArgumentList.Add(check); start.ArgumentList.Add(selfTest ? "true" : "false");
                start.ArgumentList.Add(fault ?? "");
                start.Environment.Remove("MODEL_API_KEY");
                using (Process process = Process.Start(start))
                {
                    if (!process.WaitForExit(60000)) { process.Kill(); Check(false, "LN-" + check + " timeout"); }
                    else Check(process.ExitCode == 0, "LN-" + check);
                }
            }
        }
    }
    private sealed class Stack
    {
        public WorldState World = WorldState.CreateSeeded(7L, 42L, 8, null);
        public Clock Clock;
        public CommandArbitrator Commands;
        public ActivityExecutor Executor;
        public ResidentRecord[] Residents;
        public Stack()
        {
            Clock = new Clock(World); Commands = new CommandArbitrator(World, Clock);
            Executor = new ActivityExecutor(World, Commands, Clock); Residents = World.ResidentsSnapshot();
        }
        public ExecutionRecord Direct(int index, string code, long? target)
        {
            ResidentRecord resident;
            World.TryGetResident(Residents[index].Id, out resident);
            CommandResult result = Commands.Submit(new CommandEnvelope((CommandId)World.NextCommandId,
                World.Epoch, World.GameSeconds, CommandSource.PlayerHuman, resident.ResidentSequence + 1u,
                CommandKind.DirectActivity, resident.Id, new ActivityId(code), target,
                new CommandParameters.Direct(QueueMode.Immediate)));
            Check(result.Accepted, "LN-real-COMMAND-accepted");
            ActionEntry entry = Commands.GetCurrent(resident.Id);
            Executor.Tick(Exec);
            return entry == null ? null : Executor.GetExecution(entry.Action);
        }
        public void Step()
        {
            Clock.AdvanceRealSeconds(Core, .1); Executor.Tick(Exec);
        }
    }
    private static bool Exclusive(SlotHold? hold, ResidentId owner, bool contenderHeld)
    {
        return hold.HasValue && hold.Value.Holder == owner && !contenderHeld;
    }
    private static void SlotLifecycle(ApproachPose pose, string name)
    {
        // Fresh state for each terminal boundary. No receipt can stand in for a hold.
        foreach (string terminal in new[] { "complete", "interrupt", "failure", "load" })
        {
            Stack s = new Stack(); ObjectId target = s.World.AllocateObject(Core);
            s.Executor.RegisterObject(target, new[] { pose });
            s.Executor.SetResidentPosition(s.Residents[0].Id, pose);
            s.Executor.SetResidentPosition(s.Residents[1].Id, pose);
            s.Direct(0, "ACT-EDU-02", (long)target); s.Step();
            ExecutionRecord first = s.Executor.GetResidentStatus(s.Residents[0].Id).Execution;
            s.Direct(1, "ACT-EDU-02", (long)target);
            ExecutionRecord second = s.Executor.GetResidentStatus(s.Residents[1].Id).Execution;
            bool exclusive = Exclusive(s.Executor.GetHold(target, 0), s.Residents[0].Id,
                fault == "slot" || second != null && second.Holds.Length != 0);
            Check(exclusive && first != null && first.Holds.Length == 1 && second.Queued,
                "LN-exclusive-" + name + "-" + terminal);
            if (terminal == "complete")
            {
                for (int tick = 0; tick < 700 && !first.IsTerminal; tick++) s.Step();
                Check(first.Outcome == TerminalOutcome.Completed, "LN-real-completion-" + name);
            }
            else if (terminal == "interrupt")
            {
                s.Direct(0, "ACT-HOME-01", null);
                Check(first.Outcome == TerminalOutcome.Interrupted, "LN-real-interruption-" + name);
            }
            else if (terminal == "failure")
            {
                s.Executor.MoveObject(target, new[] { pose });
                Check(first.Outcome == TerminalOutcome.Failed, "LN-real-failure-" + name);
            }
            else
            {
                s.Executor.ReconcileAfterLoad(new ActionInstanceId[0]);
                Check(s.Executor.GetExecution(first.Action) == null, "LN-load-drops-orphan-" + name);
            }
            SlotHold? after = s.Executor.GetHold(target, 0);
            Check(first.Holds.Length == 0 && (!after.HasValue || after.Value.Action != first.Action),
                "LN-release-" + terminal + "-" + name);
        }
        Stack unreachable = new Stack(); ObjectId blocked = unreachable.World.AllocateObject(Core);
        unreachable.Executor.RegisterObject(blocked, new[] { pose });
        unreachable.Executor.SetReachable(blocked, false);
        ExecutionRecord refused = unreachable.Direct(0, "ACT-EDU-02", (long)blocked);
        Check(refused.Outcome == TerminalOutcome.Failed && refused.BlockedReason == RejectReason.NoPath
            && !unreachable.Executor.GetHold(blocked, 0).HasValue, "LN-no-path-no-hold-" + name);
    }
    private static void Tiers()
    {
        Stack detailed = new Stack(), reduced = new Stack();
        Stack[] stacks = { detailed, reduced };
        PopulationScheduler[] schedulers = new PopulationScheduler[2];
        ResidentId[] visible = new ResidentId[8];
        for (int j = 0; j < 2; j++)
        {
            Stack s = stacks[j];
            foreach (ResidentRecord resident in s.Residents)
            {
                for (int n = 0; n < StateSchema.NeedCount; n++) s.World.SetNeed(Core, resident.Id, (Need)n, 80);
                Check(s.Commands.EnqueueAutonomous(resident.Id, new ActivityId("ACT-PARTY-01"), null), "LN-tier-autonomous-command");
            }
            s.Executor.Tick(Exec);
            schedulers[j] = new PopulationScheduler(s.World, s.Clock, s.Commands, s.Executor, new Autonomy(s.World, s.Commands));
        }
        for (int i = 0; i < visible.Length; i++) visible[i] = detailed.Residents[i].Id;
        TierContext all = new TierContext(visible, null, null, null);
        TierContext hidden = new TierContext(new ResidentId[0], null, null, null);
        // SDoc BP-SEC-05 hourly decay, independent of scheduler outputs.
        double[] decay = { 4,3,6,2,2,2,1,0 };
        bool correct = true;
        for (int tick = 1; tick <= 120; tick++)
        {
            schedulers[0].AdvanceRealSeconds(Core, .1, all);
            schedulers[1].AdvanceRealSeconds(Core, .1, hidden);
            long observedReducedTicks = reduced.Clock.TotalTicks - (fault == "tier" ? 1L : 0L);
            correct &= detailed.World.GameSeconds == tick*6L && reduced.World.GameSeconds == tick*6L
                && detailed.Clock.TotalTicks == tick && reduced.Clock.TotalTicks == tick
                && observedReducedTicks == tick
                && detailed.World.ComputeStateHash() == reduced.World.ComputeStateHash();
            foreach (ResidentRecord resident in reduced.World.ResidentsSnapshot())
            {
                correct &= schedulers[1].GetTier(resident.Id) == SimulationTier.Reduced;
                for (int n = 0; n < StateSchema.NeedCount; n++)
                    correct &= Math.Abs(resident.GetNeed((Need)n) - (80-decay[n]*tick*6/3600.0)) < 1e-5;
            }
        }
        Check(correct, "LN-Reduced-no-skipped-tick-no-auto-fill-independent-needs");
    }
    public static int Main(string[] args)
    {
        bool selfTest = args.Length > 0 && args[0] == "--self-test";
        fault = selfTest && args.Length > 1 ? args[1]
            : args.Length > 1 && args[0] == "negative-fixture" ? args[1]
            : args.Length > 0 && args[0] == "negative-fixture" ? "lod" : null;
        Case("LN-data", () => DataChecks(selfTest));
        Case("LN-shared-NAV-COMMAND", () => SlotLifecycle(new ApproachPose(0,0,0), "NAV-corpus-origin"));
        Case("LN-TIERS", Tiers);
        if (!selfTest) Case("LN-manifest-slots", () => {
            using (JsonDocument manifest = JsonDocument.Parse(File.ReadAllText("Assets/Blockparty/Content/navigation-lod.json")))
            {
                JsonElement slots = manifest.RootElement.GetProperty("slots");
                Check(slots.GetArrayLength() > 0, "LN-exported-slots-present");
                foreach (JsonElement slot in slots.EnumerateArray())
                {
                    JsonElement p = slot.GetProperty("approach");
                    SlotLifecycle(new ApproachPose(p[0].GetSingle(),p[1].GetSingle(),p[2].GetSingle()),slot.GetProperty("id").GetString());
                }
            }
        });
        Console.WriteLine(failures == 0 ? (selfTest ? "LOD-NAV SELF-TEST PASS" : "LOD-NAV ALL PASS") : "LOD-NAV FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
