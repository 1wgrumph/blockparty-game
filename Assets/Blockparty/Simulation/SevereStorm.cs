// STORM@1: population/age-gated severe storm, damage, weather outage and
// normal HOME/CLUB repair recovery. Durable state lives in WorldState so a
// reconstructed service resumes the same warning, event and exposure ledger.
// Plain C# 9, no engine dependency and no static mutable state.
using System;
using System.Collections.Generic;
using System.Globalization;
using Blockparty.Activities;
using Blockparty.Contracts;

namespace Blockparty.Simulation
{
    public sealed class SevereStorm
    {
        private const StateWriter Writer = StateWriter.SimCore;
        private const long DayGameSeconds = 86400L;
        private const long WarningLeadGameSeconds = 86400L;
        private const long ActiveGameSeconds = 21600L;
        private const long CooldownGameSeconds = 1728000L;
        private const int MinimumPopulation = 16;
        private const int CommonDamageCount = 2;
        private const float ExposureHealthLoss = 20.0f;
        private const string ReadyState = "Ready";
        private const string DamagedState = "Damaged";

        private readonly WorldState world;
        private readonly ActivityExecutor executor;
        private readonly Home home;
        private readonly Club club;

        public SevereStorm(WorldState world, ActivityExecutor executor, Home home, Club club)
        {
            if (world == null)
            {
                throw new ArgumentNullException("world");
            }
            if (executor == null)
            {
                throw new ArgumentNullException("executor");
            }
            if (home == null)
            {
                throw new ArgumentNullException("home");
            }
            if (club == null)
            {
                throw new ArgumentNullException("club");
            }
            this.world = world;
            this.executor = executor;
            this.home = home;
            this.club = club;
            ApplyExecutorWeather();
        }

        public long? ScheduledStartGameSeconds
        {
            get
            {
                long value = world.Storm.ScheduledStartGameSeconds;
                return value == 0L ? (long?)null : value;
            }
        }

        public EventId? ActiveEvent
        {
            get
            {
                long value = world.Storm.ActiveEvent;
                return value == 0L ? (EventId?)null : (EventId)value;
            }
        }

        public long CooldownUntilGameSeconds
        {
            get { return world.Storm.CooldownUntilGameSeconds; }
        }

        public void RegisterObject(StateWriter writer, ObjectId id, bool outdoor,
            bool electric, bool common, HomeId? roofHome)
        {
            CheckWriter(writer);
            if ((long)id <= 0L || (long)id >= world.NextObjectId)
            {
                throw new ArgumentOutOfRangeException("id", "Object is outside the allocated range");
            }
            if (roofHome.HasValue && ((long)roofHome.Value <= 0L || (long)roofHome.Value >= world.NextHomeId))
            {
                throw new ArgumentOutOfRangeException("roofHome", "Home is outside the allocated range");
            }
            StormObjectRow row = FindObject(id);
            if (row == null)
            {
                row = new StormObjectRow();
                row.Object = (long)id;
                world.Storm.Objects.Add(row);
            }
            row.Outdoor = outdoor;
            row.Electric = electric;
            row.Common = common;
            row.RoofHome = roofHome.HasValue ? (long)roofHome.Value : 0L;
            ApplyExecutorWeather();
        }

        public void SetOutdoors(StateWriter writer, ResidentId resident, bool outdoors, ObjectId? shelter)
        {
            CheckWriter(writer);
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record))
            {
                throw new ArgumentException("Unknown resident " + ((long)resident).ToString(), "resident");
            }
            if (shelter.HasValue && ((long)shelter.Value <= 0L || (long)shelter.Value >= world.NextObjectId))
            {
                throw new ArgumentOutOfRangeException("shelter", "Object is outside the allocated range");
            }
            StormResidentRow row = FindResident(resident);
            if (row == null)
            {
                row = new StormResidentRow();
                row.Resident = (long)resident;
                world.Storm.Residents.Add(row);
            }
            row.Outdoors = outdoors;
            row.Shelter = shelter.HasValue ? (long)shelter.Value : 0L;
            if (!outdoors)
            {
                row.RoutedEvent = 0L;
            }
            else if (world.Storm.ActiveEvent != 0L && row.RoutedEvent != world.Storm.ActiveEvent)
            {
                RouteToShelter(writer, row, world.Storm.ActiveEvent);
            }
        }

        public void Tick(StateWriter writer)
        {
            CheckWriter(writer);
            long now = world.GameSeconds;
            SyncRepairs(writer);
            if (world.Storm.ActiveEvent != 0L)
            {
                UpdateShelteredResidents();
                if (now >= world.Storm.ActiveEndGameSeconds)
                {
                    EndStorm();
                }
                else
                {
                    ApplyExecutorWeather();
                    return;
                }
            }

            if (world.Storm.ScheduledStartGameSeconds != 0L)
            {
                if (now >= world.Storm.ScheduledStartGameSeconds)
                {
                    StartStorm(writer);
                }
                return;
            }

            TrySchedule(writer);
        }

        public string DamageState(ObjectId id)
        {
            StormObjectRow row = FindObject(id);
            if (row == null || !row.StormDamaged || !home.IsDamaged(id))
            {
                return string.Empty;
            }
            return row.RoofHome != 0L ? "Damaged(Leak)" : DamagedState;
        }

        public bool IsElectricEnabled(ObjectId id)
        {
            StormObjectRow row = FindObject(id);
            if (row == null || !row.Electric)
            {
                return true;
            }
            return world.Storm.ActiveEvent == 0L;
        }

        private void TrySchedule(StateWriter writer)
        {
            if (world.Storm.ActiveEvent != 0L || world.Storm.ScheduledStartGameSeconds != 0L)
            {
                return;
            }
            long now = world.GameSeconds;
            if (now < 30L * DayGameSeconds
                || (world.Storm.CooldownUntilGameSeconds != 0L
                    && now < world.Storm.CooldownUntilGameSeconds)
                || LivingPopulation() < MinimumPopulation)
            {
                return;
            }
            long start = now + WarningLeadGameSeconds;
            world.Storm.ScheduledStartGameSeconds = start;
            world.AppendEvent(writer, "SevereStormWarning", null,
                "Severe Storm warning: scheduled start "
                + start.ToString(CultureInfo.InvariantCulture));
        }

        private void StartStorm(StateWriter writer)
        {
            long start = world.Storm.ScheduledStartGameSeconds;
            world.Storm.ScheduledStartGameSeconds = 0L;
            WorldEvent active = world.AppendEvent(writer, "SevereStorm", null, "storm start");
            world.Storm.ActiveEvent = (long)active.Event;
            world.Storm.ActiveEndGameSeconds = start + ActiveGameSeconds;
            DamageTargets(writer);
            ExposeOutdoors(writer, active.Event);
            RouteOutdoors(writer, (long)active.Event);
            ApplyExecutorWeather();
        }

        private void EndStorm()
        {
            long activeEvent = world.Storm.ActiveEvent;
            long end = world.Storm.ActiveEndGameSeconds;
            world.Storm.ActiveEvent = 0L;
            world.Storm.ActiveEndGameSeconds = 0L;
            world.Storm.ScheduledStartGameSeconds = 0L;
            world.Storm.CooldownUntilGameSeconds = end + CooldownGameSeconds;
            for (int i = 0; i < world.Storm.Residents.Count; i++)
            {
                if (world.Storm.Residents[i].RoutedEvent == activeEvent)
                {
                    world.Storm.Residents[i].RoutedEvent = 0L;
                }
            }
            ApplyExecutorWeather();
        }

        private void DamageTargets(StateWriter writer)
        {
            List<long> picked = new List<long>();
            for (int count = 0; count < CommonDamageCount; count++)
            {
                StormObjectRow chosen = null;
                long best = long.MaxValue;
                for (int i = 0; i < world.Storm.Objects.Count; i++)
                {
                    StormObjectRow row = world.Storm.Objects[i];
                    ObjectId id = (ObjectId)row.Object;
                    if (!row.Common || row.Object >= best || picked.Contains(row.Object)
                        || home.IsDamaged(id))
                    {
                        continue;
                    }
                    best = row.Object;
                    chosen = row;
                }
                if (chosen == null)
                {
                    break;
                }
                picked.Add(chosen.Object);
                DamageObject(writer, chosen);
            }

            long occupiedHome = LowestOccupiedHome();
            if (occupiedHome == long.MaxValue)
            {
                return;
            }
            StormObjectRow roof = null;
            long lowestRoof = long.MaxValue;
            for (int i = 0; i < world.Storm.Objects.Count; i++)
            {
                StormObjectRow row = world.Storm.Objects[i];
                if (row.RoofHome == occupiedHome && row.Object < lowestRoof)
                {
                    lowestRoof = row.Object;
                    roof = row;
                }
            }
            if (roof != null)
            {
                DamageObject(writer, roof);
            }
        }

        private void DamageObject(StateWriter writer, StormObjectRow row)
        {
            ObjectId id = (ObjectId)row.Object;
            home.SetDamaged(writer, id, true);
            row.StormDamaged = true;
            club.RegisterHelpTarget(writer, id, HouseholdForHome(row.RoofHome), DamagedState);
        }

        private void ExposeOutdoors(StateWriter writer, EventId activeEvent)
        {
            ResidentRecord[] residents = world.ResidentsSnapshot();
            for (int i = 0; i < residents.Length; i++)
            {
                ResidentRecord resident = residents[i];
                StormResidentRow place = FindResident(resident.Id);
                if (!resident.IsAlive || place == null || !place.Outdoors
                    || AlreadyExposed((long)activeEvent, (long)resident.Id))
                {
                    continue;
                }
                world.SetNeed(writer, resident.Id, Need.Health,
                    resident.GetNeed(Need.Health) - ExposureHealthLoss);
                world.SetHealthFlags(writer, resident.Id, resident.StarvationHours,
                    resident.Ill, resident.Fatigued, true, resident.LastChanceDrawDay);
                StormExposureRow exposure = new StormExposureRow();
                exposure.Event = (long)activeEvent;
                exposure.Resident = (long)resident.Id;
                world.Storm.Exposures.Add(exposure);
            }
        }

        private void RouteOutdoors(StateWriter writer, long activeEvent)
        {
            for (int i = 0; i < world.Storm.Residents.Count; i++)
            {
                StormResidentRow row = world.Storm.Residents[i];
                if (row.Outdoors && row.RoutedEvent != activeEvent)
                {
                    RouteToShelter(writer, row, activeEvent);
                }
            }
        }

        private void RouteToShelter(StateWriter writer, StormResidentRow row, long activeEvent)
        {
            row.RoutedEvent = activeEvent;
            ActionEntry route = new ActionEntry(
                world.AllocateCommand(writer),
                world.AllocateAction(writer),
                CommandKind.TravelTo,
                new ActivityId(ActivityExecutor.StormShelterActivityCode),
                row.Shelter == 0L ? (long?)null : row.Shelter,
                ActionOwner.Autonomous,
                null,
                TravelTargetKind.Object);
            executor.Dispatch((ResidentId)row.Resident, route);
        }

        private void UpdateShelteredResidents()
        {
            for (int i = 0; i < world.Storm.Residents.Count; i++)
            {
                StormResidentRow row = world.Storm.Residents[i];
                if (!row.Outdoors || row.RoutedEvent != world.Storm.ActiveEvent)
                {
                    continue;
                }
                ResidentStatus status = executor.GetResidentStatus((ResidentId)row.Resident);
                if (status.IsIdle && string.Equals(status.IdleReason, "Completed Travel", StringComparison.Ordinal))
                {
                    row.Outdoors = false;
                    row.RoutedEvent = 0L;
                }
            }
        }

        private void SyncRepairs(StateWriter writer)
        {
            for (int i = 0; i < world.Storm.Objects.Count; i++)
            {
                StormObjectRow row = world.Storm.Objects[i];
                if (!row.StormDamaged)
                {
                    continue;
                }
                ObjectId id = (ObjectId)row.Object;
                if (!home.IsDamaged(id))
                {
                    row.StormDamaged = false;
                    if (!string.Equals(club.HelpTargetState(id), ReadyState, StringComparison.Ordinal))
                    {
                        club.RegisterHelpTarget(writer, id, HouseholdForHome(row.RoofHome), ReadyState);
                    }
                }
                else if (string.Equals(club.HelpTargetState(id), ReadyState, StringComparison.Ordinal))
                {
                    home.SetDamaged(writer, id, false);
                    row.StormDamaged = false;
                }
            }
        }

        private void ApplyExecutorWeather()
        {
            List<ObjectId> blocked = new List<ObjectId>();
            if (world.Storm.ActiveEvent != 0L)
            {
                for (int i = 0; i < world.Storm.Objects.Count; i++)
                {
                    StormObjectRow row = world.Storm.Objects[i];
                    if (row.Outdoor || row.Electric)
                    {
                        blocked.Add((ObjectId)row.Object);
                    }
                }
            }
            executor.SetUnsafeWeather(Writer, blocked.ToArray());
        }

        private int LivingPopulation()
        {
            ResidentRecord[] residents = world.ResidentsSnapshot();
            int count = 0;
            for (int i = 0; i < residents.Length; i++)
            {
                if (residents[i].IsAlive)
                {
                    count++;
                }
            }
            return count;
        }

        private long LowestOccupiedHome()
        {
            long lowest = long.MaxValue;
            ResidentRecord[] residents = world.ResidentsSnapshot();
            for (int i = 0; i < residents.Length; i++)
            {
                if (residents[i].IsAlive && (long)residents[i].Home < lowest)
                {
                    lowest = (long)residents[i].Home;
                }
            }
            return lowest;
        }

        private HouseholdId HouseholdForHome(long home)
        {
            HouseholdId found = default(HouseholdId);
            ResidentRecord[] residents = world.ResidentsSnapshot();
            for (int i = 0; i < residents.Length; i++)
            {
                if (!residents[i].IsAlive || (long)residents[i].Home != home)
                {
                    continue;
                }
                if ((long)found == 0L || (long)residents[i].Household < (long)found)
                {
                    found = residents[i].Household;
                }
            }
            return found;
        }

        private StormObjectRow FindObject(ObjectId id)
        {
            for (int i = 0; i < world.Storm.Objects.Count; i++)
            {
                if (world.Storm.Objects[i].Object == (long)id)
                {
                    return world.Storm.Objects[i];
                }
            }
            return null;
        }

        private StormResidentRow FindResident(ResidentId id)
        {
            for (int i = 0; i < world.Storm.Residents.Count; i++)
            {
                if (world.Storm.Residents[i].Resident == (long)id)
                {
                    return world.Storm.Residents[i];
                }
            }
            return null;
        }

        private bool AlreadyExposed(long activeEvent, long resident)
        {
            for (int i = 0; i < world.Storm.Exposures.Count; i++)
            {
                if (world.Storm.Exposures[i].Event == activeEvent
                    && world.Storm.Exposures[i].Resident == resident)
                {
                    return true;
                }
            }
            return false;
        }

        private static void CheckWriter(StateWriter writer)
        {
            if (writer != StateWriter.SimCore && writer != StateWriter.CommandArbitrator
                && writer != StateWriter.ActivityExecutor)
            {
                throw new ArgumentException("Unknown state writer", "writer");
            }
        }
    }
}
