// SOCIAL@1: social activity outcomes (BP-ACT-06 / BP-REL-01..04).
// Follows the HOME@1/WORK@1 service shape: Social(WorldState,
// CommandArbitrator, ActivityExecutor), Tick drives the real executor and
// applies the RELATION completion boundary only on natural completion, once
// per action. Interrupted, failed and abandoned actions finish with no
// effect. Commands enter the real arbitrator; the sweep adopts social
// actions whatever submitted them. Plain C# 9, no engine dependency.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;
using Blockparty.Simulation;

namespace Blockparty.Activities
{
    public sealed class Social
    {
        private const string ChatCode = "ACT-REL-01";
        private const string FriendlyCode = "ACT-REL-02";
        private const string RomanceCode = "ACT-REL-03";
        private const string ArgumentCode = "ACT-REL-04";

        private sealed class Tracked
        {
            public ResidentId Resident;
            public ResidentId Target;
            public ActivityId Activity;
            public bool Done;
        }

        private readonly WorldState world;
        private readonly CommandArbitrator arbitrator;
        private readonly ActivityExecutor executor;
        private readonly Relationships relationships;
        private readonly Dictionary<ActionInstanceId, Tracked> tracked = new Dictionary<ActionInstanceId, Tracked>();

        public Social(WorldState world, CommandArbitrator arbitrator, ActivityExecutor executor)
        {
            if (world == null) throw new ArgumentNullException("world");
            if (arbitrator == null) throw new ArgumentNullException("arbitrator");
            if (executor == null) throw new ArgumentNullException("executor");
            this.world = world;
            this.arbitrator = arbitrator;
            this.executor = executor;
            this.relationships = new Relationships(world);
        }

        public void Tick(StateWriter writer)
        {
            CheckWriter(writer);
            Sweep();
            executor.Tick(StateWriter.ActivityExecutor);
            foreach (KeyValuePair<ActionInstanceId, Tracked> pair in tracked)
            {
                Tracked entry = pair.Value;
                if (entry.Done) continue;
                ExecutionRecord execution = executor.GetExecution(pair.Key);
                if (execution == null || !execution.IsTerminal) continue;
                entry.Done = true;
                if (execution.Outcome == TerminalOutcome.Completed)
                    relationships.Complete(writer, entry.Activity, entry.Resident, entry.Target);
            }
        }

        private void Sweep()
        {
            ResidentRecord[] residents = world.ResidentsSnapshot();
            for (int i = 0; i < residents.Length; i++)
                if (residents[i].IsAlive) Track(residents[i].Id);
        }

        private void Track(ResidentId resident)
        {
            ActionEntry current = arbitrator.GetCurrent(resident);
            if (current != null) TrackEntry(resident, current);
            ActionEntry[] pending = arbitrator.GetPending(resident);
            for (int i = 0; i < pending.Length; i++) TrackEntry(resident, pending[i]);
        }

        private void TrackEntry(ResidentId resident, ActionEntry entry)
        {
            if (entry.Kind != CommandKind.DirectActivity || !IsSocialCode(entry.Activity.Code)) return;
            if (!entry.TargetId.HasValue) return;
            if (tracked.ContainsKey(entry.Action)) return;
            Tracked next = new Tracked();
            next.Resident = resident;
            next.Target = (ResidentId)entry.TargetId.Value;
            next.Activity = entry.Activity;
            tracked.Add(entry.Action, next);
        }

        private static bool IsSocialCode(string code)
        {
            return code == ChatCode || code == FriendlyCode || code == RomanceCode || code == ArgumentCode;
        }

        private static void CheckWriter(StateWriter writer)
        {
            if (writer != StateWriter.SimCore && writer != StateWriter.CommandArbitrator && writer != StateWriter.ActivityExecutor)
                throw new ArgumentException("Unknown state writer", "writer");
        }
    }
}
