// IFC-LOCK production checks. Standalone console program: compiled and run
// with the Unity-bundled Roslyn + netcorerun until TOOLCHAIN lands the
// Unity test path. Positive checks construct every contract's boundary
// types; negative checks inject the failure and require rejection.
using System;
using System.Collections.Generic;
using System.Reflection;
using Blockparty.Contracts;

public static class IfcLockChecks
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
        Check(InterfaceVersion.Value == 1, "interface-stamp-v1");

        // Identifiers: explicit conversions round-trip; ids never implicitly convert.
        ResidentId resident = (ResidentId)7L;
        Check((long)resident == 7L, "id-explicit-roundtrip");
        Check(new ResidentId(1) != new ResidentId(2), "id-distinct");
        ActivityId act = new ActivityId("ACT-FOOD-01");
        Check(act == new ActivityId("ACT-FOOD-01"), "activity-id-equality");
        Check(act.GetHashCode() == new ActivityId("ACT-FOOD-01").GetHashCode(), "activity-id-stable-hash");

        // CONTRACT-BP-01-CMD: envelope + queue bound + stale rejection.
        CommandEnvelope cmd = new CommandEnvelope(
            (CommandId)1L, (WorldEpoch)3u, 120L, CommandSource.PlayerHuman, 9u,
            CommandKind.DirectActivity, resident, act, (long?)5L,
            new CommandParameters.Direct(QueueMode.Append));
        Check(cmd.Kind == CommandKind.DirectActivity, "cmd-envelope");
        Check(CommandQueuePolicy.MaxPending == 8, "cmd-queue-bound-8");
        Check(!CommandQueuePolicy.AppendIsFull(7) && CommandQueuePolicy.AppendIsFull(8), "cmd-ninth-queuefull");
        Check(CommandResult.Reject(RejectReason.StaleEpoch).Accepted == false, "cmd-stale-rejected");
        Check(CommandResult.Accept().Accepted, "cmd-accepted");
        CommandId other = (CommandId)2L;
        Check(CommandResult.Supersede(other).SupersededBy == other, "cmd-superseded");

        Check(new CommandParameters.Travel(QueueMode.Append, TravelTargetKind.Resident).TargetKind
            == TravelTargetKind.Resident && new CommandParameters.Travel(QueueMode.Immediate).TargetKind
            == TravelTargetKind.Unspecified, "R13-travel-target-kind-and-legacy-default");

        // CONTRACT-BP-02-EXEC: exactly-once ledger; unordered pairs normalize.
        EffectLedgerEntry entry = new EffectLedgerEntry((ActionInstanceId)11L, "Pay", new LedgerRecipient(resident));
        Check(entry.TryCommit(), "ledger-first-commit");
        Check(!entry.TryCommit(), "ledger-replay-refused");
        Check(new LedgerRecipient((ResidentId)1L, (ResidentId)2L) == new LedgerRecipient((ResidentId)2L, (ResidentId)1L), "ledger-pair-unordered");
        SlotHold hold = new SlotHold((ObjectId)4L, 0, new ApproachPose(1.0f, 0.0f, 2.0f), resident, (ActionInstanceId)11L);
        Check(hold.SlotIndex == 0 && hold.Holder == resident, "slot-hold");

        // CONTRACT-BP-03-TIME: fixed tick; pause means zero advance.
        Check(SimTick.TickGameSeconds == 6 && SimTick.TicksPerGameMinute == 10, "clock-tick");
        ClockState paused = new ClockState(600L, SimulationSpeed.Normal, true);
        Check(paused.Paused && (int)SimulationSpeed.Ultra == 7, "clock-pause-flag");

        // CONTRACT-BP-04-D1U: freshness match; stale generations rejected.
        FreshnessStamp v1 = new FreshnessStamp((WorldEpoch)1u, 5u, 2, (HomeId)3L, 6u, 1u);
        FreshnessStamp stale = new FreshnessStamp((WorldEpoch)1u, 5u, 2, (HomeId)3L, 7u, 1u);
        Check(v1.Matches(v1), "freshness-match");
        DecisionProposal proposal = new DecisionProposal((GatewayRequestId)1L, act, null, stale);
        Check(!proposal.IsFresh(v1), "d1u-stale-proposal-rejected");
        Check(GatewayLimits.ServiceTimeoutRealSeconds == 10
            && GatewayLimits.DecisionFallbackGameMinutes == 3
            && GatewayLimits.MaxConcurrentPerDecisionPurpose == 2
            && GatewayLimits.MaxConcurrentGameWide == 8, "d1u-limits");
        Check(GatewayLimits.DecisionLogMaxRecords == 1024, "decision-log-bound");

        // CONTRACT-BP-05-IMG: work item refs; no second item on duplicate result.
        WorkItemRef work = new WorkItemRef((ObjectId)9L, resident, "summary", (EventId)2L);
        Check(work.Author == resident && CreativeWork.MakeWallArtActivity == "ACT-CREA-01", "img-workitem");

        // CONTRACT-BP-06-VOICE: ambiguous/empty text executes nothing.
        Check(!new VoiceTranscript("", null).ExecutesCommand, "voice-empty-executes-nothing");
        Check(!new VoiceTranscript("maybe do a thing", null).ExecutesCommand, "voice-ambiguous-executes-nothing");
        Check(new VoiceTranscript("pause", VoiceMeaning.Pause).ExecutesCommand, "voice-mapped-executes");
        Check(Enum.GetNames(typeof(VoiceMeaning)).Length == 10, "voice-ten-meanings");

        // CONTRACT-BP-07-SAM: zero matches as 0; image coordinates only.
        SamResult empty = new SamResult((CaptureId)1L, new List<SamOutline>());
        Check(empty.Outlines.Count == 0, "sam-zero-matches");
        Check(NoIdentityFields(typeof(SamOutline)) && NoIdentityFields(typeof(SamResult)), "sam-no-identity-fields");

        // CONTRACT-BP-08-RESOLVE: read-only snapshots; same names never collapse.
        ResidentSummary a = new ResidentSummary((ResidentId)1L, "Alex");
        ResidentSummary b = new ResidentSummary((ResidentId)2L, "Alex");
        Check(a.Id != b.Id && a.DisplayName == b.DisplayName, "resolve-duplicates-distinct");
        Check(typeof(IResidentDirectory).IsInterface, "resolve-query-role");

        // CONTRACT-BP-09-SAVE: v1/v2 supported, unknown refused; atomic names.
        Check(new SaveHeader(1, 1L, (WorldEpoch)1u, 60L, "abc").IsSupported, "save-v1-supported");
        Check(new SaveHeader(2, 1L, (WorldEpoch)1u, 60L, "abc").IsSupported, "save-v2-supported");
        Check(!new SaveHeader(3, 1L, (WorldEpoch)1u, 60L, "abc").IsSupported, "save-unknown-refused");
        Check(SaveFormat.FileName == "save.json" && SaveFormat.TempName == "save.tmp" && SaveFormat.BackupName == "save.bak", "save-atomic-names");

        // CONTRACT-BP-10-ASSET: paid-only and unregistered excluded.
        Check(!new AssetRegistration("a1", "ed", "CC0", "by", true, true, false).ExcludedFromSubmission, "asset-free-included");
        Check(new AssetRegistration("a2", "ed", "paid", "", false, false, true).ExcludedFromSubmission, "asset-paid-excluded");
        Check(new AssetRegistration("", "", "", "", false, false, false).ExcludedFromSubmission, "asset-unregistered-excluded");

        // CONTRACT-BP-11-BUILD: identity recordable on every claim.
        BuildIdentity build = new BuildIdentity("b1", "6000.3.10f1", "deadbeef", "Linux-x86_64");
        Check(build.UnityVersion.Length > 0 && build.CommitHash.Length > 0, "build-identity");

        // CONTRACT-BP-12-LIFE: pregnancy/will/estate/care keyed by EventId.
        PregnancyRecord preg = new PregnancyRecord(resident, (ResidentId)3L, (EventId)5L, 4320L, 1);
        Check(preg.DueGameTime == 4320L, "life-pregnancy");
        WillRecord will = new WillRecord(
            new List<MoneyShare> { new MoneyShare("h1", 60), new MoneyShare("Charity", 40) },
            100, new List<ObjectId>(), "", "", new List<HouseholdId>());
        Check(will.MoneyTotalPercent() == 100, "life-will-totals");
        EstateLedgerEntry estate = new EstateLedgerEntry((EventId)6L, "home:1", "h2", false);
        Check(!estate.Settled && (long)estate.DeathEvent == 6L, "life-estate-keyed");
        CareAssignment care = new CareAssignment((ResidentId)4L, (ResidentId)5L, new CarePlacement((HouseholdId)2L, 0), (EventId)7L);
        Check(care.Placement.Household.HasValue, "life-care");

        Console.WriteLine(failures == 0 ? "IFC-LOCK ALL PASS" : "IFC-LOCK FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }

    // SAM contract carries image coordinates only: fail if any identity,
    // location or activity field appears on the type.
    private static bool NoIdentityFields(Type type)
    {
        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            string name = field.FieldType.FullName ?? string.Empty;
            if (name.IndexOf("ResidentId", StringComparison.Ordinal) >= 0
                || name.IndexOf("ObjectId", StringComparison.Ordinal) >= 0
                || name.IndexOf("ActivityId", StringComparison.Ordinal) >= 0
                || name.IndexOf("HomeId", StringComparison.Ordinal) >= 0)
            {
                return false;
            }
        }
        return true;
    }
}
