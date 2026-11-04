using System;
using System.Collections.Generic;
using Blockparty.Contracts;

namespace Blockparty.Simulation
{
    // Call after each shared-clock advance, before changing the aging preset.
    public sealed class Aging
    {
        private readonly WorldState world;
        private readonly Clock clock;
        private long lastTime;
        private readonly Dictionary<ResidentId, LifeStage> stages = new Dictionary<ResidentId, LifeStage>();
        private readonly Dictionary<ResidentId, double> progress = new Dictionary<ResidentId, double>();

        public Aging(WorldState world, Clock clock)
        {
            if (world == null) throw new ArgumentNullException("world");
            if (clock == null) throw new ArgumentNullException("clock");
            if (clock.World != world) throw new ArgumentException("Clock belongs to another world", "clock");
            this.world = world;
            this.clock = clock;
            lastTime = world.GameSeconds;
            foreach (ResidentRecord resident in world.ResidentsSnapshot()) Remember(resident);
        }

        private void Remember(ResidentRecord resident)
        {
            stages[resident.Id] = resident.Stage;
            progress[resident.Id] = resident.HasAgingProgress
                && (float)resident.AgingProgressDays == resident.StageProgressDays
                ? resident.AgingProgressDays : resident.StageProgressDays;
        }

        public void Tick(StateWriter writer)
        {
            long elapsed = world.GameSeconds - lastTime;
            lastTime = world.GameSeconds;
            foreach (ResidentRecord resident in world.ResidentsSnapshot())
            {
                LifeStage previous;
                if (!stages.TryGetValue(resident.Id, out previous) || previous != resident.Stage)
                {
                    Remember(resident);
                    continue;
                }
                if (!resident.IsAlive || !Clockwork.AgingAdvances(clock.Preset) || elapsed == 0) continue;
                double days = progress[resident.Id] + (double)elapsed / Clockwork.GameSecondsPerGameDay
                    / Clockwork.PresetMultiplier(clock.Preset);
                LifeStage stage = resident.Stage;
                double remainder = resident.ElderRemainderDays;
                bool frail = resident.IsFrail;
                while (true)
                {
                    if (stage == LifeStage.Elder && remainder == 0.0)
                    {
                        var rng = new WorldRng(unchecked(world.Seed + 7919L * (long)resident.Id));
                        remainder = 31.5 * (0.75 + 0.5 * rng.NextDouble());
                    }
                    double duration = stage == LifeStage.Elder ? remainder : Clockwork.ModerateStageDays(stage);
                    if (days < duration) break;
                    if (stage == LifeStage.Elder)
                    {
                        days = duration;
                        frail = true;
                        world.SetAlive(writer, resident.Id, false);
                        world.AppendEvent(writer, "Death", resident.Id, "OldAge");
                        break;
                    }
                    days -= duration;
                    stage = (LifeStage)((int)stage + 1);
                    world.AppendEvent(writer, "Birthday", resident.Id, stage.ToString());
                }
                if (stage == LifeStage.Elder && days >= remainder - 1.0) frail = true;
                world.SetStage(writer, resident.Id, stage, (float)days);
                world.SetAging(writer, resident.Id, days, remainder, frail);
                stages[resident.Id] = stage;
                progress[resident.Id] = days;
            }
        }
    }
}
