// CLOCK production checks. Standalone console program: compiled and run with
// the Unity-bundled Roslyn + netcorerun until TOOLCHAIN lands the Unity test
// path. Positive checks prove one shared clock (fixed 6-game-second ticks,
// 1:3:7 speeds, 24-real-minute day), pause freeze, no catch-up by
// construction, aging-fraction preservation and the 29.9-33.1h child+teen
// pacing rate using stage-boundary fixtures. Mirrors the STATE pattern.
using System;
using System.Reflection;
using Blockparty.Contracts;
using Blockparty.Simulation;

public static class ClockChecks
{
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

    public static int Main()
    {
        Check(InterfaceVersion.Value == 1, "interface-v1");
        Check(SimTick.TickGameSeconds == 6L && SimTick.TicksPerGameMinute == 10, "tick-base-6s");

        // One game day takes 24 unpaused real minutes at Normal.
        WorldState day = NewWorld(11L, LifeStage.Adult, 0.0f);
        Clock dayClock = new Clock(day);
        dayClock.AdvanceRealSeconds(StateWriter.SimCore, 1440.0);
        Check(day.GameSeconds == 86400L && dayClock.TotalTicks == 14400L, "day-length-24min");

        // Speeds scale one clock 1:3:7; 3 real seconds give 3/9/21 game min.
        WorldState wn = NewWorld(12L, LifeStage.Adult, 0.0f);
        WorldState wf = NewWorld(13L, LifeStage.Adult, 0.0f);
        WorldState wu = NewWorld(14L, LifeStage.Adult, 0.0f);
        Clock cn = new Clock(wn);
        Clock cf = new Clock(wf);
        Clock cu = new Clock(wu);
        cf.SetSpeed(SimulationSpeed.Fast);
        cu.SetSpeed(SimulationSpeed.Ultra);
        cn.AdvanceRealSeconds(StateWriter.SimCore, 3.0);
        cf.AdvanceRealSeconds(StateWriter.SimCore, 3.0);
        cu.AdvanceRealSeconds(StateWriter.SimCore, 3.0);
        Check(wn.GameSeconds == 180L && wf.GameSeconds == 540L && wu.GameSeconds == 1260L, "speed-ratios-1-3-7");
        Check(cn.TotalTicks == 30L && cf.TotalTicks == 90L && cu.TotalTicks == 210L, "tick-counts-30-90-210");

        // One tick is 6 game seconds; sub-tick remainders accumulate.
        WorldState wt = NewWorld(15L, LifeStage.Adult, 0.0f);
        Clock ct = new Clock(wt);
        ct.AdvanceRealSeconds(StateWriter.SimCore, 0.1);
        Check(wt.GameSeconds == 6L && ct.TotalTicks == 1L, "tick-is-6s");
        ct.AdvanceRealSeconds(StateWriter.SimCore, 0.05);
        Check(wt.GameSeconds == 6L && ct.TotalTicks == 1L, "subtick-waits");
        ct.AdvanceRealSeconds(StateWriter.SimCore, 0.05);
        Check(wt.GameSeconds == 12L && ct.TotalTicks == 2L, "subtick-accumulates");

        // Tick count is independent of frame chunking.
        WorldState wa = NewWorld(16L, LifeStage.Child, 5.0f);
        WorldState wb = NewWorld(16L, LifeStage.Child, 5.0f);
        Clock ca = new Clock(wa);
        Clock cb = new Clock(wb);
        for (int i = 0; i < 60; i++)
        {
            ca.AdvanceRealSeconds(StateWriter.SimCore, 0.05);
        }
        cb.AdvanceRealSeconds(StateWriter.SimCore, 3.0);
        double chunkGap = (double)ChildProgress(wa) - (double)ChildProgress(wb);
        if (chunkGap < 0.0)
        {
            chunkGap = -chunkGap;
        }
        Check(wa.GameSeconds == wb.GameSeconds && ca.TotalTicks == cb.TotalTicks
            && chunkGap < 0.0001, "frame-chunks-identical");

        // Pause freezes clock, ticks and aging; remainder survives the pause.
        WorldState wp = NewWorld(17L, LifeStage.Child, 6.0f);
        Clock cp = new Clock(wp);
        cp.AdvanceRealSeconds(StateWriter.SimCore, 0.05);
        int hashBeforePause = wp.ComputeStateHash();
        cp.Pause();
        for (int i = 0; i < 600; i++)
        {
            cp.AdvanceRealSeconds(StateWriter.SimCore, 0.1);
        }
        Check(wp.ComputeStateHash() == hashBeforePause && wp.GameSeconds == 0L
            && cp.TotalTicks == 0L && ChildProgress(wp) == 6.0f, "pause-freezes");
        cp.Resume();
        cp.AdvanceRealSeconds(StateWriter.SimCore, 0.05);
        Check(wp.GameSeconds == 6L && cp.TotalTicks == 1L, "pause-keeps-remainder");

        // Speed controls: Paused parks, explicit speed resumes.
        WorldState ws = NewWorld(18L, LifeStage.Adult, 0.0f);
        Clock cs = new Clock(ws);
        cs.SetSpeed(SimulationSpeed.Fast);
        cs.SetSpeed(SimulationSpeed.Paused);
        Check(cs.Paused && cs.Speed == SimulationSpeed.Fast, "set-speed-paused-parks");
        cs.AdvanceRealSeconds(StateWriter.SimCore, 10.0);
        Check(ws.GameSeconds == 0L, "parked-advances-nothing");
        cs.Resume();
        cs.AdvanceRealSeconds(StateWriter.SimCore, 1.0);
        Check(!cs.Paused && ws.GameSeconds == 180L, "resume-restores-speed");
        cs.Pause();
        cs.SetSpeed(SimulationSpeed.Normal);
        Check(!cs.Paused && cs.Speed == SimulationSpeed.Normal, "set-speed-unpauses");
        ClockState snap = cs.Snapshot();
        Check(snap.GameSeconds == ws.GameSeconds && snap.Speed == SimulationSpeed.Normal && !snap.Paused, "snapshot-matches");

        // Preset switches preserve the completed fraction; Off holds it.
        WorldState wr = NewWorld(19L, LifeStage.Child, 12.6f);
        Clock cr = new Clock(wr);
        double fraction = Clockwork.StageFraction((double)ChildProgress(wr), Clockwork.ModerateStageDays(LifeStage.Child));
        Check(Close(fraction, 0.4), "fraction-0.4");
        Check(Close(Clockwork.RemainingDays(0.4, 31.5, AgingPreset.Moderate), 18.9)
            && Close(Clockwork.RemainingDays(0.4, 31.5, AgingPreset.Slow), 75.6)
            && Close(Clockwork.RemainingDays(0.4, 31.5, AgingPreset.Accelerated), 9.45), "remainder-values");
        AgingPreset[] cycle = new AgingPreset[] { AgingPreset.Slow, AgingPreset.Accelerated, AgingPreset.Off, AgingPreset.Moderate };
        for (int i = 0; i < 20; i++)
        {
            cr.SetPreset(cycle[i % cycle.Length]);
        }
        Check(ChildProgress(wr) == 12.6f, "fraction-preserved-across-presets");
        Check(Close(Clockwork.RemainingDays(Clockwork.StageFraction(12.6, 31.5), 31.5, AgingPreset.Moderate), 18.9), "remainder-never-compounds");
        cr.SetPreset(AgingPreset.Off);
        cr.AdvanceRealSeconds(StateWriter.SimCore, 1440.0);
        Check(ChildProgress(wr) == 12.6f && wr.GameSeconds == 86400L, "off-holds-fraction");
        cr.SetPreset(AgingPreset.Moderate);
        cr.AdvanceRealSeconds(StateWriter.SimCore, 1440.0);
        Check(ChildProgress(wr) == 13.6f, "moderate-resumes-from-fraction");

        // Aging rides the same clock: Fast ages 3x per real second.
        WorldState gn = NewWorld(20L, LifeStage.Child, 0.0f);
        WorldState gf = NewWorld(21L, LifeStage.Child, 0.0f);
        Clock hn = new Clock(gn);
        Clock hf = new Clock(gf);
        hf.SetSpeed(SimulationSpeed.Fast);
        hn.AdvanceRealSeconds(StateWriter.SimCore, 1440.0);
        hf.AdvanceRealSeconds(StateWriter.SimCore, 1440.0);
        Check(Close((double)ChildProgress(gn), 1.0) && Close((double)ChildProgress(gf), 3.0), "aging-scales-with-speed");

        // Child plus teen pacing at Normal/Moderate: 78.75 game days is
        // 113400 real seconds = 31.5 h, inside 29.9-33.1 h. A 600 s pause
        // inside Teen adds no aging.
        WorldState pace = NewWorld(22L, LifeStage.Child, 0.0f);
        Clock pc = new Clock(pace);
        ResidentRecord pacer = pace.ResidentsSnapshot()[0];
        double realSeconds = 0.0;
        pc.AdvanceRealSeconds(StateWriter.SimCore, 45360.0);
        realSeconds += 45360.0;
        Check(StageOf(pace, pacer.Id) == LifeStage.Child && ChildProgress(pace) == 31.5f, "child-boundary-exact");
        Check(realSeconds == 45360.0, "child-takes-45360s");
        pace.SetStage(StateWriter.SimCore, pacer.Id, LifeStage.Teen, 0.0f);
        pc.Pause();
        pc.AdvanceRealSeconds(StateWriter.SimCore, 600.0);
        Check(ChildProgress(pace) == 0.0f, "pause-adds-no-aging");
        pc.Resume();
        pc.AdvanceRealSeconds(StateWriter.SimCore, 68040.0);
        realSeconds += 68040.0;
        Check(ChildProgress(pace) == 47.25f, "teen-boundary-exact");
        double hours = realSeconds / 3600.0;
        Check(realSeconds == 113400.0 && hours == 31.5, "child-teen-31.5h");
        Check(hours >= 29.9 && hours <= 33.1, "pacing-inside-tolerance");
        Check(Clockwork.RealHoursForGameDays(78.75) == 31.5, "pacing-arithmetic");

        // Boundary clamps: no stage rewrite, no transition (AGING owns it).
        pc.AdvanceRealSeconds(StateWriter.SimCore, 144000.0);
        Check(StageOf(pace, pacer.Id) == LifeStage.Teen && ChildProgress(pace) == 47.25f, "boundary-clamps");

        // Gestation due-time is conception plus exactly 3 unpaused game days.
        Check(Clockwork.GestationDueGameSeconds(1000000L) == 1259200L, "gestation-due-plus-3d");
        WorldState wg = NewWorld(23L, LifeStage.Adult, 0.0f);
        Clock cg = new Clock(wg);
        cg.AdvanceRealSeconds(StateWriter.SimCore, 60.0);
        long due = Clockwork.GestationDueGameSeconds(wg.GameSeconds);
        long gapBefore = due - wg.GameSeconds;
        cg.Pause();
        cg.AdvanceRealSeconds(StateWriter.SimCore, 600.0);
        cg.SetPreset(AgingPreset.Accelerated);
        Check(due - wg.GameSeconds == gapBefore && gapBefore == 259200L, "gestation-ignores-pause-preset");

        // The dead do not age.
        WorldState wd = NewWorld(24L, LifeStage.Child, 10.0f);
        ResidentRecord deceased = wd.ResidentsSnapshot()[0];
        wd.SetAlive(StateWriter.SimCore, deceased.Id, false);
        Clock cd = new Clock(wd);
        cd.AdvanceRealSeconds(StateWriter.SimCore, 1440.0);
        Check(ChildProgress(wd) == 10.0f, "dead-not-aged");

        // No catch-up: save/load resumes the exact saved time and progress.
        WorldSnapshot saved = pace.ExportSnapshot();
        WorldState loaded = WorldState.LoadFromSnapshot(saved);
        Clock lc = new Clock(loaded);
        Check(loaded.GameSeconds == pace.GameSeconds && ChildProgress(loaded) == ChildProgress(pace)
            && lc.TotalTicks == 0L, "reload-resumes-exact-time");

        // Tick remainder accessor: a restored clock advances identically to
        // the uninterrupted reference at every fractional edge.
        WorldState rk = NewWorld(26L, LifeStage.Adult, 0.0f);
        Clock ck = new Clock(rk);
        ck.SetSpeed(SimulationSpeed.Fast);
        ck.AdvanceRealSeconds(StateWriter.SimCore, 0.07);
        WorldState rk2 = WorldState.LoadFromSnapshot(rk.ExportSnapshot());
        Clock ck2 = new Clock(rk2);
        ck2.SetSpeed(SimulationSpeed.Fast);
        ck2.RestoreTickState(ck.TickRemainder, ck.TotalTicks);
        Check(ck2.TickRemainder == ck.TickRemainder && ck2.TotalTicks == ck.TotalTicks, "remainder-restores-exact");
        bool restoredParity = true;
        for (int edge = 0; edge < 20; edge++)
        {
            ck.AdvanceRealSeconds(StateWriter.SimCore, 0.07);
            ck2.AdvanceRealSeconds(StateWriter.SimCore, 0.07);
            if (rk.GameSeconds != rk2.GameSeconds || ck.TotalTicks != ck2.TotalTicks)
            {
                restoredParity = false;
            }
        }
        Check(restoredParity, "restored-clock-matches-reference");

        // Fast Enter Play Mode: no static mutable state in the package.
        Check(NoStaticMutableState(typeof(Clock).Assembly), "femp-no-static-mutable");

        // ---- negatives: injected failures must be refused ----
        WorldState neg = NewWorld(25L, LifeStage.Child, 1.0f);
        Clock nc = new Clock(neg);
        int hashBeforeNegatives = neg.ComputeStateHash();
        ExpectThrow(delegate { nc.AdvanceRealSeconds(StateWriter.SimCore, -1.0); }, "negative-real-rejected");
        ExpectThrow(delegate { nc.AdvanceRealSeconds(StateWriter.SimCore, double.NaN); }, "nan-real-rejected");
        ExpectThrow(delegate { nc.AdvanceRealSeconds(StateWriter.SimCore, double.PositiveInfinity); }, "inf-real-rejected");
        ExpectThrow(delegate { nc.AdvanceRealSeconds((StateWriter)99, 1.0); }, "invalid-writer-rejected");
        ExpectThrow(delegate { nc.SetSpeed((SimulationSpeed)99); }, "invalid-speed-rejected");
        ExpectThrow(delegate { nc.SetPreset((AgingPreset)99); }, "invalid-preset-rejected");
        ExpectThrow(delegate { Clockwork.PresetMultiplier(AgingPreset.Off); }, "off-has-no-multiplier");
        ExpectThrow(delegate { Clockwork.RemainingDays(0.4, 31.5, AgingPreset.Off); }, "remaining-off-rejected");
        ExpectThrow(delegate { Clockwork.SpeedMultiplier((SimulationSpeed)99); }, "multiplier-unknown-speed-rejected");
        ExpectThrow(delegate { Clockwork.ModerateStageDays((LifeStage)99); }, "schedule-unknown-stage-rejected");
        ExpectThrow(delegate { new Clock(null); }, "null-world-rejected");
        ExpectThrow(delegate { nc.RestoreTickState(double.NaN, 0L); }, "nan-remainder-rejected");
        ExpectThrow(delegate { nc.RestoreTickState(-0.1, 0L); }, "negative-remainder-rejected");
        ExpectThrow(delegate { nc.RestoreTickState(1.0, 0L); }, "unit-remainder-rejected");
        ExpectThrow(delegate { nc.RestoreTickState(0.5, -1L); }, "negative-ticks-rejected");
        Check(neg.ComputeStateHash() == hashBeforeNegatives && nc.TotalTicks == 0L
            && nc.TickRemainder == 0.0, "negatives-mutate-nothing");

        Console.WriteLine(failures == 0 ? "CLOCK ALL PASS" : "CLOCK FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }

    private static WorldState NewWorld(long worldId, LifeStage stage, float progressDays)
    {
        WorldState world = WorldState.CreateSeeded(worldId, worldId * 7919L + 13L, 0, null);
        HomeRecord home = world.AllocateHome(StateWriter.SimCore, 4);
        HouseholdRecord household = world.AllocateHousehold(StateWriter.SimCore, "H", home.Id);
        ResidentRecord resident = world.AllocateResident(StateWriter.SimCore, "R", stage, household.Id, home.Id);
        if (progressDays != 0.0f)
        {
            world.SetStage(StateWriter.SimCore, resident.Id, stage, progressDays);
        }
        return world;
    }

    private static float ChildProgress(WorldState world)
    {
        return world.ResidentsSnapshot()[0].StageProgressDays;
    }

    private static LifeStage StageOf(WorldState world, ResidentId id)
    {
        ResidentRecord record;
        world.TryGetResident(id, out record);
        return record.Stage;
    }

    private static bool Close(double actual, double expected)
    {
        double gap = actual - expected;
        if (gap < 0.0)
        {
            gap = -gap;
        }
        return gap < 0.000001;
    }

    private static void ExpectThrow(Action action, string name)
    {
        try
        {
            action();
            failures++;
            Console.WriteLine("FAIL " + name);
        }
        catch (Exception)
        {
            Console.WriteLine("PASS " + name);
        }
    }

    // Fast Enter Play Mode forbids static mutable state: every static field in
    // this package must be const or readonly (domain reload off keeps statics).
    private static bool NoStaticMutableState(Assembly assembly)
    {
        foreach (Type type in assembly.GetTypes())
        {
            if (type.Namespace == null || !type.Namespace.StartsWith("Blockparty.", StringComparison.Ordinal))
            {
                continue;
            }
            FieldInfo[] fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].IsLiteral)
                {
                    continue;
                }
                if (!fields[i].IsInitOnly)
                {
                    Console.WriteLine("mutable static: " + type.FullName + "." + fields[i].Name);
                    return false;
                }
            }
        }
        return true;
    }
}
