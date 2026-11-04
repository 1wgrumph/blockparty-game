// UI-NAV@1: world-map navigation controller (BP-UI-03/05/06 over NAV/COMMAND).
// Consumes STATE@1 (WorldState, CONTRACT-BP-08-RESOLVE name query), COMMAND@1
// (CommandArbitrator TravelTo submission) and NAV@2 (ActivityExecutor positions,
// reachability, in-flight activity). Plain C# 9, no UnityEngine dependency.
// Map/selection/follow/Refresh are presentation-only instance state; TravelTo
// preflights target and reachability, then submits one command, never
// SetResidentPosition for movement. No static mutable state: fresh instances
// start with no map/selection/follow state (Fast Enter Play Mode, reload off).
using System;
using System.Collections.Generic;
using Blockparty.Contracts;
using Blockparty.Simulation;

namespace Blockparty.UI
{
    // One search row: authoritative identity/home plus live NAV position and
    // the current in-flight activity, or null when idle or travelling.
    public readonly struct NavigationResident
    {
        public readonly ResidentId Id;
        public readonly string DisplayName;
        public readonly HomeId Home;
        public readonly ApproachPose Position;
        public readonly ActivityId? Activity;

        public NavigationResident(ResidentId id, string displayName, HomeId home,
            ApproachPose position, ActivityId? activity)
        {
            Id = id;
            DisplayName = displayName ?? string.Empty;
            Home = home;
            Position = position;
            Activity = activity;
        }
    }

    // One neighborhood-map pin: the BLOCK-SITES destination id, its
    // registered entry object and the entry approach pose.
    public sealed class MapPin
    {
        public MapPin(string id, ObjectId obj, ApproachPose entry)
        {
            Id = id;
            Object = obj;
            Entry = entry;
        }

        public string Id { get; private set; }
        public ObjectId Object { get; private set; }
        public ApproachPose Entry { get; private set; }
    }

    public sealed class WorldNavigation
    {
        private static readonly IReadOnlyList<MapPin> NoPins =
            new List<MapPin>().AsReadOnly();

        private readonly WorldState world;
        private readonly CommandArbitrator commands;
        private readonly ActivityExecutor executor;
        private readonly BlockSites sites;
        private bool mapOpen;
        private ResidentId? selected;
        private ResidentId? followed;
        private string followName;
        private string message;

        public WorldNavigation(WorldState world, CommandArbitrator commands, ActivityExecutor executor)
        {
            if (world == null)
            {
                throw new ArgumentNullException("world");
            }
            if (commands == null)
            {
                throw new ArgumentNullException("commands");
            }
            if (executor == null)
            {
                throw new ArgumentNullException("executor");
            }
            this.world = world;
            this.commands = commands;
            this.executor = executor;
            this.sites = null;
        }

        // BLOCK-SITES@1: same controller over the registered in-block
        // destinations; MapPins then lists one pin per site.
        public WorldNavigation(WorldState world, CommandArbitrator commands,
            ActivityExecutor executor, BlockSites sites)
        {
            if (world == null)
            {
                throw new ArgumentNullException("world");
            }
            if (commands == null)
            {
                throw new ArgumentNullException("commands");
            }
            if (executor == null)
            {
                throw new ArgumentNullException("executor");
            }
            if (sites == null)
            {
                throw new ArgumentNullException("sites");
            }
            this.world = world;
            this.commands = commands;
            this.executor = executor;
            this.sites = sites;
        }

        public IReadOnlyList<MapPin> MapPins
        {
            get
            {
                if (sites == null)
                {
                    return NoPins;
                }
                List<MapPin> pins = new List<MapPin>(sites.Sites.Count);
                for (int i = 0; i < sites.Sites.Count; i++)
                {
                    BlockSite site = sites.Sites[i];
                    pins.Add(new MapPin(site.Id, site.Object, site.Entry));
                }
                return pins.AsReadOnly();
            }
        }

        public bool IsMapOpen { get { return mapOpen; } }
        public ResidentId? SelectedResident { get { return selected; } }
        public ResidentId? FollowedResident { get { return followed; } }
        public string Message { get { return message; } }

        public void OpenMap()
        {
            mapOpen = true;
        }

        public void CloseMap()
        {
            mapOpen = false;
        }

        public void StopFollowing()
        {
            followed = null;
            followName = null;
        }

        // Re-read live authority; a followed resident who died or vanished
        // ends follow with a named notice. Mutates presentation state only.
        public void Refresh()
        {
            if (!followed.HasValue)
            {
                return;
            }
            ResidentRecord record;
            if (world.TryGetResident(followed.Value, out record) && record.IsAlive)
            {
                return;
            }
            string name = record != null ? record.DisplayName : followName;
            followed = null;
            followName = null;
            message = (name ?? string.Empty) + " has died; following stopped.";
        }

        // Authoritative exact-name query; a miss clears selection with notice.
        public IReadOnlyList<NavigationResident> SearchByName(string name)
        {
            IReadOnlyList<ResidentSummary> summaries = world.SearchByName(name);
            List<NavigationResident> rows = new List<NavigationResident>(summaries.Count);
            for (int i = 0; i < summaries.Count; i++)
            {
                ResidentRecord record;
                if (world.TryGetResident(summaries[i].Id, out record))
                {
                    rows.Add(Snapshot(record));
                }
            }
            if (rows.Count == 0)
            {
                selected = null;
                message = "No match for \"" + (name ?? string.Empty) + "\".";
            }
            return rows.AsReadOnly();
        }

        // Residents currently performing the activity, by friendly name or
        // catalog code. A miss clears selection with notice, like name search.
        public IReadOnlyList<NavigationResident> SearchByActivity(string nameOrCode)
        {
            string code = ResolveActivityCode(nameOrCode);
            List<NavigationResident> rows = new List<NavigationResident>();
            ResidentRecord[] residents = world.ResidentsSnapshot();
            for (int i = 0; i < residents.Length; i++)
            {
                ResidentStatus status = executor.GetResidentStatus(residents[i].Id);
                if (status.Execution != null && status.Execution.Stage == ExecutionStage.Perform
                    && string.Equals(status.Execution.Activity.Code, code, StringComparison.Ordinal))
                {
                    rows.Add(Snapshot(residents[i]));
                }
            }
            if (rows.Count == 0)
            {
                selected = null;
                message = "No match for \"" + (nameOrCode ?? string.Empty) + "\".";
            }
            return rows.AsReadOnly();
        }

        public CommandResult SelectResident(ResidentId id)
        {
            ResidentRecord record;
            if (!world.TryGetResident(id, out record))
            {
                message = "Unknown resident " + ((long)id).ToString() + ".";
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            selected = id;
            return CommandResult.Accept();
        }

        public CommandResult FollowResident(ResidentId id)
        {
            ResidentRecord record;
            if (!world.TryGetResident(id, out record))
            {
                message = "Unknown resident " + ((long)id).ToString() + ".";
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            followed = id;
            followName = record.DisplayName;
            return CommandResult.Accept();
        }

        // One map travel order: preflight identity, target and reachability
        // without touching sequences or queues, then submit via COMMAND/NAV.
        public CommandResult TravelTo(ResidentId traveller, long targetId, QueueMode mode)
        {
            ResidentRecord destination;
            bool resident = world.TryGetResident((ResidentId)targetId, out destination);
            bool obj = IsKnownObject(targetId);
            if (selected.HasValue && (long)selected.Value == targetId && resident)
                return TravelTo(traveller, targetId, mode, TravelTargetKind.Resident);
            if (resident && obj)
            {
                message = "Travel refused: select a resident or specify an object target.";
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            return TravelTo(traveller, targetId, mode,
                resident ? TravelTargetKind.Resident : TravelTargetKind.Object);
        }

        public CommandResult TravelTo(ResidentId traveller, long targetId, QueueMode mode, TravelTargetKind targetKind)
        {
            ResidentRecord record;
            if (!world.TryGetResident(traveller, out record))
            {
                message = "Unknown resident " + ((long)traveller).ToString() + ".";
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            if (!record.IsAlive)
            {
                message = record.DisplayName + " has died; cannot travel.";
                return CommandResult.Reject(RejectReason.ResidentDeceased);
            }
            if (targetKind != TravelTargetKind.Object && targetKind != TravelTargetKind.Resident)
            {
                message = "Travel refused: target kind required.";
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            if (targetKind == TravelTargetKind.Object && IsUnreachableObject(targetId))
            {
                message = "Travel refused: NoPath.";
                return CommandResult.Reject(RejectReason.NoPath);
            }
            ResidentRecord destination;
            if (targetKind == TravelTargetKind.Object ? !IsKnownObject(targetId)
                : !world.TryGetResident((ResidentId)targetId, out destination))
            {
                message = "Travel refused: target missing.";
                return CommandResult.Reject(RejectReason.TargetMissing);
            }
            if (mode == QueueMode.Append && commands.GetPending(traveller).Length >= CommandQueuePolicy.MaxPending)
                return CommandResult.Reject(RejectReason.QueueFull);
            CommandResult result = commands.Submit(new CommandEnvelope(
                world.AllocateCommand(StateWriter.SimCore),
                world.Epoch,
                world.GameSeconds,
                CommandSource.PlayerHuman,
                record.ResidentSequence + 1u,
                CommandKind.TravelTo,
                traveller,
                null,
                targetId,
                new CommandParameters.Travel(mode, targetKind)));
            if (!result.Accepted)
            {
                message = "Travel refused: " + result.Reason.ToString() + ".";
            }
            return result;
        }

        private NavigationResident Snapshot(ResidentRecord record)
        {
            return new NavigationResident(record.Id, record.DisplayName, record.Home,
                executor.GetResidentPosition(record.Id), CurrentActivity(record.Id));
        }

        private ActivityId? CurrentActivity(ResidentId id)
        {
            ResidentStatus status = executor.GetResidentStatus(id);
            if (status.Execution == null || string.IsNullOrEmpty(status.Execution.Activity.Code))
            {
                return null;
            }
            return status.Execution.Activity;
        }

        // Probe object registration through the published reachability query.
        private bool IsUnreachableObject(long targetId)
        {
            try
            {
                return !executor.IsReachable((ObjectId)targetId);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private bool IsKnownObject(long targetId)
        {
            try
            {
                executor.IsReachable((ObjectId)targetId);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        // SDoc section 5 catalog entry ACT-FOOD-01 CookAtHome is the cooking
        // activity; consumed APIs expose codes only, so the friendly name
        // maps here and anything else passes through as a literal code.
        private static string ResolveActivityCode(string nameOrCode)
        {
            if (string.Equals(nameOrCode, "Cooking", StringComparison.OrdinalIgnoreCase))
            {
                return ActivityExecutor.CookActivityCode;
            }
            return nameOrCode ?? string.Empty;
        }
    }
}
