// CLOCK@1: the one shared simulation clock (SIM-CORE time authority).
// Consumes IFC-LOCK@1 (SimulationSpeed, AgingPreset, ClockState, SimTick)
// and STATE@1 (WorldState, LifeStage, StateWriter). Plain C# 9, no
// UnityEngine dependency. Every authoritative mutation flows through an
// audited WorldState method with a StateWriter (BP-SYS-02).
// Fast Enter Play Mode: no static mutable state (consts only), explicit
// numeric conversions, no AppDomain/Assembly.Location/BinaryFormatter, no
// wall-clock reads, no String.GetHashCode.
// Time base (BP-SEC-05): at Normal, 1 real second advances 1 game minute,
// so 1 game day takes 24 unpaused real minutes; speeds multiply 1:3:7 and
// Pause is 0. Ticks are fixed 6 game seconds (SimTick), accumulated from
// real seconds so any frame chunking yields identical ticks.
// Advance applies whole ticks in aggregate: clock and aging are linear in
// ticks, so one SetGameSeconds plus one SetStage per resident is exactly
// what a per-tick loop would commit. Aging clamps at the stage boundary;
// transitions, the once-drawn elder remainder and Frail belong to AGING.
// Needs, activities, gestation arrival and progression consume this tick
// stream in their own packages; gestation due-time is conception plus
// exactly 3 game days of unpaused clock (pure helper below).
// No closed-game catch-up by construction: this file never reads a wall
// clock; time only enters through AdvanceRealSeconds, and a loaded world
// resumes at its saved GameSeconds.
using System;
using Blockparty.Contracts;

namespace Blockparty.Simulation
{
    // Pure time/aging math over the IFC-LOCK boundary types.
    public static class Clockwork
    {
        public const long GameSecondsPerGameMinute = 60L;
        public const long GameSecondsPerGameDay = 86400L;
        public const double GameSecondsPerRealSecondAtNormal = 60.0;
        public const double RealSecondsPerGameDayAtNormal = 1440.0;
        public const double TicksPerRealSecondAtNormal = 10.0;
        public const long GestationGameSeconds = 259200L;

        public static long SpeedMultiplier(SimulationSpeed speed)
        {
            switch (speed)
            {
                case SimulationSpeed.Paused: return 0L;
                case SimulationSpeed.Normal: return 1L;
                case SimulationSpeed.Fast: return 3L;
                case SimulationSpeed.Ultra: return 7L;
                default: throw new ArgumentOutOfRangeException("speed", "Unknown simulation speed");
            }
        }

        // Candidate Moderate schedule in game days (BP-SEC-05). Fractional
        // days are retained; Elder here is the reference until AGING owns
        // the once-drawn remainder.
        public static double ModerateStageDays(LifeStage stage)
        {
            switch (stage)
            {
                case LifeStage.Newborn: return 2.25;
                case LifeStage.Infant: return 11.25;
                case LifeStage.Toddler: return 15.75;
                case LifeStage.Child: return 31.5;
                case LifeStage.Teen: return 47.25;
                case LifeStage.YoungAdult: return 63.0;
                case LifeStage.Adult: return 94.5;
                case LifeStage.Elder: return 31.5;
                default: throw new ArgumentOutOfRangeException("stage", "Unknown life stage");
            }
        }

        public static bool AgingAdvances(AgingPreset preset)
        {
            switch (preset)
            {
                case AgingPreset.Off: return false;
                case AgingPreset.Slow: return true;
                case AgingPreset.Moderate: return true;
                case AgingPreset.Accelerated: return true;
                default: throw new ArgumentOutOfRangeException("preset", "Unknown aging preset");
            }
        }

        public static double PresetMultiplier(AgingPreset preset)
        {
            switch (preset)
            {
                case AgingPreset.Slow: return 4.0;
                case AgingPreset.Moderate: return 1.0;
                case AgingPreset.Accelerated: return 0.5;
                case AgingPreset.Off:
                default: throw new ArgumentOutOfRangeException("preset", "Off holds the fraction; it has no duration multiplier");
            }
        }

        public static double StageFraction(double progressDays, double baseDays)
        {
            if (baseDays <= 0.0)
            {
                throw new ArgumentOutOfRangeException("baseDays", "Stage duration must be positive");
            }
            double fraction = progressDays / baseDays;
            if (fraction < 0.0)
            {
                return 0.0;
            }
            if (fraction > 1.0)
            {
                return 1.0;
            }
            return fraction;
        }

        // Remaining game days from the stored Moderate base, never by
        // compounding an already scaled remainder (BP-TIME-04).
        public static double RemainingDays(double fraction, double baseDays, AgingPreset preset)
        {
            if (fraction < 0.0 || fraction > 1.0)
            {
                throw new ArgumentOutOfRangeException("fraction", "Fraction must be within [0,1]");
            }
            if (baseDays <= 0.0)
            {
                throw new ArgumentOutOfRangeException("baseDays", "Stage duration must be positive");
            }
            return (1.0 - fraction) * baseDays * PresetMultiplier(preset);
        }

        public static long GestationDueGameSeconds(long conceptionGameSeconds)
        {
            if (conceptionGameSeconds < 0L)
            {
                throw new ArgumentOutOfRangeException("conceptionGameSeconds", "Conception time must be non-negative");
            }
            return conceptionGameSeconds + GestationGameSeconds;
        }

        public static double RealHoursForGameDays(double gameDays)
        {
            return gameDays * 0.4;
        }
    }

    // The running clock bound to one world. Speed/pause/preset are control
    // inputs held here until SAVE persists them, with the sub-tick remainder
    // and tick count; world time and aging progress live in the WorldState
    // and move only through Advance.
    public sealed class Clock
    {
        private readonly WorldState world;
        private SimulationSpeed speed;
        private bool paused;
        private AgingPreset preset;
        private double tickRemainder;
        private long totalTicks;

        public Clock(WorldState world)
        {
            if (world == null)
            {
                throw new ArgumentNullException("world");
            }
            this.world = world;
            speed = SimulationSpeed.Normal;
            paused = false;
            preset = AgingPreset.Moderate;
            tickRemainder = 0.0;
            totalTicks = 0L;
        }

        public WorldState World { get { return world; } }
        public SimulationSpeed Speed { get { return speed; } }
        public bool Paused { get { return paused; } }
        public AgingPreset Preset { get { return preset; } }
        public long TotalTicks { get { return totalTicks; } }
        public double TickRemainder { get { return tickRemainder; } }

        public void SetSpeed(SimulationSpeed value)
        {
            Clockwork.SpeedMultiplier(value);
            if (value == SimulationSpeed.Paused)
            {
                paused = true;
                return;
            }
            speed = value;
            paused = false;
        }

        public void Pause()
        {
            paused = true;
        }

        public void Resume()
        {
            paused = false;
        }

        public void SetPreset(AgingPreset value)
        {
            if (!Enum.IsDefined(typeof(AgingPreset), value))
            {
                throw new ArgumentOutOfRangeException("value", "Unknown aging preset");
            }
            preset = value;
        }

        // SAVE restores the exact sub-tick fraction and tick count so a
        // loaded world advances identically to the uninterrupted reference.
        public void RestoreTickState(double remainder, long savedTotalTicks)
        {
            if (double.IsNaN(remainder) || double.IsInfinity(remainder)
                || remainder < 0.0 || remainder >= 1.0)
            {
                throw new ArgumentOutOfRangeException("remainder", "Tick remainder must be within [0,1)");
            }
            if (savedTotalTicks < 0L)
            {
                throw new ArgumentOutOfRangeException("savedTotalTicks", "Total ticks must be non-negative");
            }
            tickRemainder = remainder;
            totalTicks = savedTotalTicks;
        }

        public ClockState Snapshot()
        {
            return new ClockState(world.GameSeconds, speed, paused);
        }

        public void AdvanceRealSeconds(StateWriter writer, double realSeconds)
        {
            AdvanceRealSeconds(writer, realSeconds, null);
        }

        internal void AdvanceRealSeconds(StateWriter writer, double realSeconds, Action tick)
        {
            CheckWriter(writer);
            if (double.IsNaN(realSeconds) || double.IsInfinity(realSeconds) || realSeconds < 0.0)
            {
                throw new ArgumentOutOfRangeException("realSeconds", "Advance amount must be finite and non-negative");
            }
            if (paused)
            {
                return;
            }
            long multiplier = Clockwork.SpeedMultiplier(speed);
            double exact = realSeconds * Clockwork.TicksPerRealSecondAtNormal * (double)multiplier + tickRemainder;
            long wholeTicks = (long)Math.Floor(exact);
            tickRemainder = exact - (double)wholeTicks;
            if (wholeTicks <= 0L)
            {
                return;
            }
            if (tick != null)
            {
                for (long i = 0; i < wholeTicks; i++)
                {
                    AdvanceTicks(writer, 1L);
                    tick();
                }
            }
            else AdvanceTicks(writer, wholeTicks);
        }

        private void AdvanceTicks(StateWriter writer, long wholeTicks)
        {
            long elapsedGameSeconds = wholeTicks * SimTick.TickGameSeconds;
            totalTicks += wholeTicks;
            world.SetGameSeconds(writer, world.GameSeconds + elapsedGameSeconds);
            if (!Clockwork.AgingAdvances(preset))
            {
                return;
            }
            double elapsedDays = (double)elapsedGameSeconds / (double)Clockwork.GameSecondsPerGameDay;
            double presetMultiplier = Clockwork.PresetMultiplier(preset);
            ResidentRecord[] residents = world.ResidentsSnapshot();
            for (int i = 0; i < residents.Length; i++)
            {
                ResidentRecord record = residents[i];
                if (!record.IsAlive)
                {
                    continue;
                }
                double baseDays = Clockwork.ModerateStageDays(record.Stage);
                double advanced = (double)record.StageProgressDays + elapsedDays / presetMultiplier;
                if (advanced > baseDays)
                {
                    advanced = baseDays;
                }
                float next = (float)advanced;
                if (next != record.StageProgressDays)
                {
                    world.SetStage(writer, record.Id, record.Stage, next);
                }
            }
        }

        private static void CheckWriter(StateWriter writer)
        {
            if (writer != StateWriter.SimCore && writer != StateWriter.CommandArbitrator && writer != StateWriter.ActivityExecutor)
            {
                throw new ArgumentException("Unknown state writer", "writer");
            }
        }
    }
}
