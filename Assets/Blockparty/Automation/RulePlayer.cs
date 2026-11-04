// BOT@1: rule-based automated player over the normal command API.
// Consumes COMMAND@2 (CommandArbitrator, ActionEntry) and IFC-LOCK@1 command/
// freshness DTOs. Plain C# 9, no UnityEngine dependency. Decide is a pure
// state read; Apply revalidates the full freshness stamp and automation
// enablement before Submit, so stale generations (including control-only
// takeovers) are rejected without touching world state; Tick drives idle
// residents only while enabled and unpaused. No static mutable state, so no
// Fast Enter Play Mode reset obligation; command ids come from an instance
// counter starting above fixture/world ranges.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;
using Blockparty.Simulation;

namespace Blockparty.Automation
{
    // Player-visible decision input only: identity, freshness, needs in Need
    // enum order, and the offered actions. No ResidentRecord, WorldState,
    // traits, diagnostic context or future-event schedule.
    public sealed class PlayerObservation
    {
        public readonly ResidentId Resident;
        public readonly FreshnessStamp Version;
        public readonly long GameSeconds;
        public readonly IReadOnlyList<float> Needs;
        public readonly IReadOnlyList<AvailableAction> Available;

        public PlayerObservation(ResidentId resident, FreshnessStamp version, long gameSeconds,
            IReadOnlyList<float> needs, IReadOnlyList<AvailableAction> available)
        {
            Resident = resident;
            Version = version;
            GameSeconds = gameSeconds;
            Needs = needs;
            Available = available;
        }
    }

    public sealed class RuleDecision
    {
        public readonly CommandEnvelope Command;
        public readonly FreshnessStamp Version;

        public RuleDecision(CommandEnvelope command, FreshnessStamp version)
        {
            Command = command;
            Version = version;
        }
    }

    public sealed class RulePlayer
    {
        private readonly CommandArbitrator arbitrator;

        public RulePlayer(CommandArbitrator arbitrator)
        {
            if (arbitrator == null)
            {
                throw new ArgumentNullException("arbitrator");
            }
            this.arbitrator = arbitrator;
        }

        // Pure state read: picks the first offered activity with its target
        // and builds the PlayerAutomated DirectActivity envelope for the next
        // resident sequence at the current epoch and game seconds. No choices
        // means no command. Writes nothing, to the world or the arbitrator.
        public RuleDecision Decide(PlayerObservation observation)
        {
            if (observation == null)
            {
                throw new ArgumentNullException("observation");
            }
            if (observation.Available == null || observation.Available.Count == 0)
            {
                return null;
            }
            AvailableAction choice = observation.Available[0];
            CommandEnvelope command = new CommandEnvelope(
                arbitrator.AllocateRuleCommand(),
                arbitrator.World.Epoch,
                arbitrator.World.GameSeconds,
                CommandSource.PlayerAutomated,
                observation.Version.Sequence + 1u,
                CommandKind.DirectActivity,
                observation.Resident,
                choice.Activity,
                choice.TargetId,
                new CommandParameters.Direct(QueueMode.Append));
            return new RuleDecision(command, observation.Version);
        }

        // Revalidates epoch, the full stamp and automation enablement before
        // Submit. Old epochs are StaleEpoch; any other version drift or a
        // disabled bot is StaleGeneration; malformed envelopes flow to the
        // arbitrator, which rejects them InvalidParameters without throwing.
        // Every rejection leaves world and queue state unchanged.
        public CommandResult Apply(RuleDecision decision)
        {
            if (decision == null)
            {
                throw new ArgumentNullException("decision");
            }
            CommandEnvelope command = decision.Command;
            if (command == null)
            {
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            if (command.Epoch != arbitrator.World.Epoch)
            {
                return CommandResult.Reject(RejectReason.StaleEpoch);
            }
            if (command.Resident.HasValue)
            {
                ResidentRecord record;
                if (arbitrator.World.TryGetResident(command.Resident.Value, out record)
                    && !decision.Version.Matches(arbitrator.CurrentStamp(command.Resident.Value)))
                {
                    return CommandResult.Reject(RejectReason.StaleGeneration);
                }
            }
            if (command.Source == CommandSource.PlayerAutomated && !arbitrator.AutomationEnabled)
            {
                return CommandResult.Reject(RejectReason.StaleGeneration);
            }
            return arbitrator.Submit(command);
        }

        // Drives idle residents through Decide/Apply only while automation is
        // enabled and the clock runs. Residents with a current action or any
        // pending work are left alone; after a manual takeover nothing is
        // issued until explicit EnableAutomation.
        public void Tick(IReadOnlyList<PlayerObservation> observations)
        {
            if (observations == null)
            {
                throw new ArgumentNullException("observations");
            }
            if (!arbitrator.AutomationEnabled || arbitrator.Clock.Paused)
            {
                return;
            }
            for (int i = 0; i < observations.Count; i++)
            {
                PlayerObservation observation = observations[i];
                if (observation == null)
                {
                    continue;
                }
                if (arbitrator.GetCurrent(observation.Resident) != null
                    || arbitrator.GetPending(observation.Resident).Length != 0)
                {
                    continue;
                }
                RuleDecision decision = Decide(observation);
                if (decision != null)
                {
                    Apply(decision);
                }
            }
        }
    }
}
