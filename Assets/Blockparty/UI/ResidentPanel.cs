// UI-INSPECT@1: resident inspection panel over the simulation API.
// Consumes STATE@1 (WorldState, ResidentRecord, MemoryEntry), COMMAND@2
// (CommandArbitrator, ActionEntry) and IFC-LOCK@1 command DTOs. Plain C# 9,
// no UnityEngine dependency: scene/view wiring belongs to GREYBOX.
// TryInspect/TryExplain are pure reads returning detached copies; only
// Cancel submits a PlayerHuman command through the arbitrator. Refused
// reads and stale cancels return a typed reason and mutate nothing, not
// even the command log. No static mutable state, so no Fast Enter Play
// Mode reset obligation; envelope ids come from an instance counter above
// fixture/world ranges, so the panel itself never writes world data.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;
using Blockparty.Simulation;

namespace Blockparty.UI
{
    // Detached read-only projection: refreshed from authority on every
    // TryInspect, never aliased to live world or queue state.
    public sealed class ResidentPanelSnapshot
    {
        public ResidentRecord Resident { get; }
        public ActionEntry Current { get; }
        public IReadOnlyList<ActionEntry> Queue { get; }

        public ResidentPanelSnapshot(ResidentRecord resident, ActionEntry current,
            IReadOnlyList<ActionEntry> queue)
        {
            Resident = resident;
            Current = current;
            Queue = queue;
        }
    }

    public sealed class ResidentPanel
    {
        private const long FirstCommandId = 2000000000L;

        private readonly WorldState world;
        private readonly CommandArbitrator arbitrator;
        private long nextCommandId = FirstCommandId;

        public ResidentPanel(WorldState world, CommandArbitrator arbitrator)
        {
            if (world == null)
            {
                throw new ArgumentNullException("world");
            }
            if (arbitrator == null)
            {
                throw new ArgumentNullException("arbitrator");
            }
            this.world = world;
            this.arbitrator = arbitrator;
        }

        // Refresh needs, queue, personality, feelings and history from
        // authority by id, including retained dead residents. Unknown
        // residents are refused without touching state, RNG or queues.
        public bool TryInspect(ResidentId resident, out ResidentPanelSnapshot snapshot,
            out string reason)
        {
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record))
            {
                snapshot = null;
                reason = "UnknownResident " + ((long)resident).ToString();
                return false;
            }
            ActionEntry[] pending = arbitrator.GetPending(resident);
            List<ActionEntry> queue = new List<ActionEntry>(pending.Length);
            for (int i = 0; i < pending.Length; i++)
            {
                queue.Add(pending[i]);
            }
            snapshot = new ResidentPanelSnapshot(CloneResident(record),
                arbitrator.GetCurrent(resident), queue.AsReadOnly());
            reason = string.Empty;
            return true;
        }

        // Detached factual memory joined to an existing event: original
        // participants, time, place, outcome and LearnedBy preserved.
        // Unknown residents, missing events and memories the resident never
        // learned (including dangling writes) are refused unchanged.
        public bool TryExplain(ResidentId resident, EventId sourceEvent,
            out MemoryEntry explanation, out string reason)
        {
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record))
            {
                explanation = null;
                reason = "UnknownResident " + ((long)resident).ToString();
                return false;
            }
            MemoryEntry memory = null;
            for (int i = 0; i < record.Memories.Count; i++)
            {
                if (record.Memories[i].Event == sourceEvent)
                {
                    memory = record.Memories[i];
                    break;
                }
            }
            if (memory == null || !HasEvent(sourceEvent))
            {
                explanation = null;
                reason = "UnknownEvent " + ((long)sourceEvent).ToString()
                    + " for resident " + ((long)resident).ToString();
                return false;
            }
            List<ResidentId> participants = new List<ResidentId>(memory.Participants.Count);
            for (int i = 0; i < memory.Participants.Count; i++)
            {
                participants.Add(memory.Participants[i]);
            }
            explanation = new MemoryEntry(memory.Event, memory.Kind, memory.GameSeconds,
                memory.Place, participants.AsReadOnly(), memory.OutcomeCode, memory.LearnedBy);
            reason = string.Empty;
            return true;
        }

        // Resolve the selected action id and cancel it as the player: the
        // current action via CancelCurrent, a queued entry via CancelQueued
        // against its CommandId. Unknown residents, deceased residents and
        // unknown/stale actions are rejected directly with a typed reason,
        // so a refusal never logs or mutates authoritative state.
        public CommandResult Cancel(ResidentId resident, ActionInstanceId action)
        {
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record))
            {
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            if (!record.IsAlive)
            {
                return CommandResult.Reject(RejectReason.ResidentDeceased);
            }
            ActionEntry current = arbitrator.GetCurrent(resident);
            if (current != null && current.Action == action)
            {
                return arbitrator.Submit(new CommandEnvelope(
                    (CommandId)(nextCommandId++),
                    world.Epoch,
                    world.GameSeconds,
                    CommandSource.PlayerHuman,
                    record.ResidentSequence + 1u,
                    CommandKind.CancelCurrent,
                    resident,
                    null,
                    null,
                    CommandParameters.None.Instance));
            }
            ActionEntry[] pending = arbitrator.GetPending(resident);
            for (int i = 0; i < pending.Length; i++)
            {
                if (pending[i].Action == action)
                {
                    return arbitrator.Submit(new CommandEnvelope(
                        (CommandId)(nextCommandId++),
                        world.Epoch,
                        world.GameSeconds,
                        CommandSource.PlayerHuman,
                        record.ResidentSequence + 1u,
                        CommandKind.CancelQueued,
                        resident,
                        null,
                        (long)pending[i].Command,
                        CommandParameters.None.Instance));
                }
            }
            return CommandResult.Reject(RejectReason.InvalidParameters);
        }

        private bool HasEvent(EventId sourceEvent)
        {
            for (int i = 0; i < world.Events.Count; i++)
            {
                if (world.Events[i].Event == sourceEvent)
                {
                    return true;
                }
            }
            return false;
        }

        // Full detach: scalars, needs, traits and cloned lists, mirroring
        // the World's own resident copy so later authority writes never
        // reach an issued projection.
        private static ResidentRecord CloneResident(ResidentRecord record)
        {
            ResidentRecord copy = new ResidentRecord(record.Id, record.DisplayName,
                record.Stage, record.Household, record.Home);
            copy.SetMoney(record.Money);
            copy.SetCareer(record.JobId, record.CareerLevel, record.CareerPoints);
            copy.SetEducation(record.EducationPoints);
            copy.SetAlive(record.IsAlive);
            copy.SetResidentSequence(record.ResidentSequence);
            copy.SetDecisionSlotGeneration(record.DecisionSlotGeneration);
            copy.SetControlGeneration(record.ControlGeneration);
            copy.SetStage(record.Stage, record.StageProgressDays);
            copy.SetHealthFlags(record.StarvationHours, record.Ill, record.Fatigued,
                record.Injured, record.LastChanceDrawDay);
            for (int i = 0; i < StateSchema.NeedCount; i++)
            {
                copy.SetNeed(i, record.GetNeed((Need)i));
            }
            for (int i = 0; i < StateSchema.TraitCount; i++)
            {
                copy.SetTrait(i, record.GetTrait((TraitAxis)i));
            }
            for (int i = 0; i < record.Skills.Count; i++)
            {
                copy.SkillList.Add(record.Skills[i].Clone());
            }
            for (int i = 0; i < record.Bonds.Count; i++)
            {
                copy.BondList.Add(record.Bonds[i].Clone());
            }
            for (int i = 0; i < record.Family.Count; i++)
            {
                copy.FamilyList.Add(record.Family[i].Clone());
            }
            for (int i = 0; i < record.Feelings.Count; i++)
            {
                copy.FeelingList.Add(record.Feelings[i].Clone());
            }
            for (int i = 0; i < record.Memories.Count; i++)
            {
                MemoryEntry memory = record.Memories[i];
                List<ResidentId> participants = new List<ResidentId>(memory.Participants.Count);
                for (int p = 0; p < memory.Participants.Count; p++)
                {
                    participants.Add(memory.Participants[p]);
                }
                copy.MemoryList.Add(new MemoryEntry(memory.Event, memory.Kind, memory.GameSeconds,
                    memory.Place, participants.AsReadOnly(), memory.OutcomeCode, memory.LearnedBy));
            }
            for (int i = 0; i < record.CompletedCourses.Count; i++)
            {
                copy.AddCompletedCourse(record.CompletedCourses[i]);
            }
            return copy;
        }
    }
}
