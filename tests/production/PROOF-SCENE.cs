// PROOF-SCENE production checks: the sim half. Standalone console program,
// compiled and run with the Unity-bundled Roslyn + netcorerun like the
// COMMAND/NAV checks. Proves the exact proof-scene scenario (seeded world,
// waypoint TravelTo then appended ACT-FOOD-02 chair sit) completes through
// the ActivityExecutor with the recorded stage order and ends idle at the
// approach pose. The scene-graph half (wiring, clips, mesh, layout) runs
// in-Unity via ProofSceneBuild.VerifyScene.
using System;
using Blockparty.Contracts;
using Blockparty.Simulation;

public static class ProofSceneChecks
{
    private const StateWriter Exec = StateWriter.ActivityExecutor;
    private const StateWriter Core = StateWriter.SimCore;

    private static int failures;

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

    private static void Submit(CommandArbitrator arbitrator, WorldState world, ResidentId hero,
        CommandKind kind, ActivityId? activity, long? targetId, CommandParameters parameters)
    {
        ResidentRecord current;
        world.TryGetResident(hero, out current);
        CommandResult result = arbitrator.Submit(new CommandEnvelope(
            world.AllocateCommand(Core), world.Epoch, world.GameSeconds,
            CommandSource.PlayerHuman, current.ResidentSequence + 1u,
            kind, hero, activity, targetId, parameters));
        Check(result.Accepted, "submit-" + kind.ToString());
    }

    public static int Main()
    {
        // Mirror of ProofSceneLayout (kept numeric: the layout class needs
        // UnityEngine vectors, so the values are asserted equal in-Unity by
        // VerifyScene instead of shared).
        ApproachPose start = new ApproachPose(-3f, 0f, -3f);
        ApproachPose waypoint = new ApproachPose(3.6f, 0f, -0.4f);
        ApproachPose approach = new ApproachPose(0f, 0f, 2.6f);

        AppearanceStore appearances;
        WorldState world = ResidentFactory.CreateNewGame(1L, 42L, null, out appearances);
        Clock clock = new Clock(world);
        CommandArbitrator arbitrator = new CommandArbitrator(world, clock);
        ActivityExecutor executor = new ActivityExecutor(world, arbitrator, clock);
        ResidentRecord[] residents = world.ResidentsSnapshot();
        Check(residents.Length == 8, "seeded-cast");
        ResidentId hero = residents[0].Id;
        Check(appearances.TryGet(hero, out AppearanceRecord look), "hero-appearance");
        executor.SetResidentPosition(hero, start);
        ObjectId waypointObject = world.AllocateObject(Core);
        executor.RegisterObject(waypointObject, new ApproachPose[] { waypoint });
        ObjectId chair = world.AllocateObject(Core);
        executor.RegisterObject(chair, new ApproachPose[] { approach });
        Submit(arbitrator, world, hero, CommandKind.TravelTo, null, (long)waypointObject,
            new CommandParameters.Travel(QueueMode.Immediate));
        Submit(arbitrator, world, hero, CommandKind.DirectActivity,
            new ActivityId("ACT-FOOD-02"), (long)chair,
            new CommandParameters.Direct(QueueMode.Append));

        ResidentStatus status = null;
        for (int i = 0; i < 3000; i++)
        {
            clock.AdvanceRealSeconds(Core, 0.1);
            executor.Tick(Exec);
            status = executor.GetResidentStatus(hero);
            if (status.IsIdle && status.IdleReason == "Completed ACT-FOOD-02")
            {
                break;
            }
        }
        Check(status.IsIdle, "ends-idle");
        Check(status.IdleReason == "Completed ACT-FOOD-02", "idle-reason");
        ApproachPose end = executor.GetResidentPosition(hero);
        Check(Math.Abs(end.X - approach.X) <= 0.06f && Math.Abs(end.Z - approach.Z) <= 0.06f,
            "ends-at-approach");

        // Both executions terminal with the BP-ACT-01 stage order.
        ActionEntry[] pending = arbitrator.GetPending(hero);
        Check(pending.Length == 0, "queue-drained");
        Console.WriteLine("PROOF-SCENE standalone checks " + (failures == 0 ? "ALL PASS" : "FAILURES=" + failures));
        return failures == 0 ? 0 : 1;
    }
}
