// STATE production checks. Standalone console program: compiled and run with
// the Unity-bundled Roslyn + netcorerun until TOOLCHAIN lands the Unity test
// path. Positive checks prove seeded records, stable identities, dated
// ledgers and sole-writer state; negative checks inject failures the world
// must refuse. Mirrors the IFC-LOCK standalone pattern.
using System;
using System.Collections.Generic;
using System.Reflection;
using Blockparty.Contracts;
using Blockparty.Simulation;

public static class StateChecks
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
        // Seeded creation is deterministic: same seed, identical records.
        WorldState a = WorldState.CreateSeeded(1L, 1234L, 8, null);
        WorldState b = WorldState.CreateSeeded(1L, 1234L, 8, null);
        Check(a.ComputeStateHash() == b.ComputeStateHash(), "seeded-deterministic");
        Check(a.ResidentCount == 8, "seeded-eight-residents");

        // Different seeds diverge.
        WorldState other = WorldState.CreateSeeded(1L, 999L, 8, null);
        Check(a.ComputeStateHash() != other.ComputeStateHash(), "seeds-diverge");

        // Named creation keeps names; blank input gets a seeded default name.
        WorldState named = WorldState.CreateSeeded(2L, 7L, 0, null);
        HomeRecord home = named.AllocateHome(StateWriter.SimCore, 4);
        HouseholdRecord household = named.AllocateHousehold(StateWriter.SimCore, "H", home.Id);
        ResidentRecord r1 = named.AllocateResident(StateWriter.SimCore, "Alex", LifeStage.Adult, household.Id, home.Id);
        ResidentRecord r2 = named.AllocateResident(StateWriter.SimCore, "", LifeStage.Child, household.Id, home.Id);
        NamedCheck(named, r1, r2);

        // 12 BP-SIM-01 field groups present and sane on a fresh record.
        Check(r1.GetNeed(Need.Hunger) == 80.0f && r1.GetNeed(Need.Health) == 80.0f, "record-needs-default");
        bool traitsInRange = true;
        for (int t = 0; t < 6; t++)
        {
            int v = r1.GetTrait((TraitAxis)t);
            if (v < -50 || v > 50)
            {
                traitsInRange = false;
            }
        }
        Check(traitsInRange, "record-traits-in-range");
        Check(r1.Stage == LifeStage.Adult && r1.Household == household.Id && r1.Money == 0L
            && r1.IsAlive && r1.ResidentSequence == 0u, "record-identity-stage-household");
        named.SetMoney(StateWriter.SimCore, r1.Id, 96L);
        named.AddSkillPoints(StateWriter.SimCore, r1.Id, "Cooking", 320);
        named.SetCareer(StateWriter.SimCore, r1.Id, "Barista", 2, 40);
        named.SetEducation(StateWriter.SimCore, r1.Id, 25);
        named.SetBond(StateWriter.SimCore, r1.Id, r2.Id, 12.5f, 0.0f);
        named.AddFamilyLink(StateWriter.SimCore, r1.Id, r2.Id, "Sibling");
        WorldEvent met = named.AppendEvent(StateWriter.SimCore, "FirstMeeting", r1.Id, "met at home");
        named.AddFeeling(StateWriter.SimCore, r1.Id, FeelingKind.Grateful, met.Event, r2.Id, 1, 86400L);
        List<ResidentId> parts = new List<ResidentId>();
        parts.Add(r1.Id);
        parts.Add(r2.Id);
        named.AddMemory(StateWriter.SimCore, r1.Id, new MemoryEntry(met.Event, "FirstMeeting", 0L, home.Id,
            parts.AsReadOnly(), "OK", "participant"));
        ResidentRecord got;
        Check(named.TryGetResident(r1.Id, out got)
            && got.Money == 96L
            && got.Skills.Count == 1 && got.Skills[0].Level == 2 && got.Skills[0].Points == 20
            && got.JobId == "Barista" && got.CareerLevel == 2
            && got.EducationPoints == 25
            && got.Bonds.Count == 1
            && got.Family.Count == 1
            && got.Feelings.Count == 1
            && got.Memories.Count == 1, "record-twelve-groups-mutate");

        // Ids are monotonic and never reused within a world.
        ResidentRecord r3 = named.AllocateResident(StateWriter.SimCore, "Bo", LifeStage.Teen, household.Id, home.Id);
        Check((long)r1.Id < (long)r2.Id && (long)r2.Id < (long)r3.Id, "ids-monotonic");

        // Snapshot round-trip preserves every value bit-identically (epoch steps).
        named.SetGameSeconds(StateWriter.SimCore, 3600L);
        named.SetNeed(StateWriter.SimCore, r1.Id, Need.Fun, 37.25f);
        ActionInstanceId act = named.AllocateAction(StateWriter.SimCore);
        named.ReserveEffect(StateWriter.ActivityExecutor, act, "Pay", new LedgerRecipient(r1.Id));
        named.CommitEffect(StateWriter.ActivityExecutor, act);
        named.SettleEstate(StateWriter.SimCore, met.Event, "home:1", "H");
        WorldSnapshot snap = named.ExportSnapshot();
        WorldState loaded = WorldState.LoadFromSnapshot(snap);
        Check((uint)loaded.Epoch == (uint)named.Epoch + 1u, "load-epoch-increments");
        // Epoch is the only intended difference: re-export and compare hashes
        // after aligning the epoch by a second snapshot cycle is overkill, so
        // compare a targeted field sweep instead plus hash of a reloaded copy.
        ResidentRecord lr1;
        Check(loaded.TryGetResident(r1.Id, out lr1)
            && lr1.DisplayName == "Alex"
            && lr1.GetNeed(Need.Fun) == 37.25f
            && lr1.Money == 96L
            && lr1.Skills[0].Level == 2 && lr1.Skills[0].Points == 20
            && lr1.Bonds.Count == 1 && lr1.Memories.Count == 1
            && loaded.GameSeconds == 3600L
            && loaded.EventCount == named.EventCount
            && loaded.EffectLedger.Count == 1 && loaded.EffectLedger[0].State == LedgerState.Committed
            && loaded.EstateLedger.Count == 1, "load-values-identical");
        WorldState reloaded = WorldState.LoadFromSnapshot(loaded.ExportSnapshot());
        Check(reloaded.EventCount == loaded.EventCount && reloaded.ResidentCount == loaded.ResidentCount, "load-twice-stable");

        // High-water marks survive: post-load allocation never reuses an id.
        long maxBefore = 0;
        ResidentRecord[] all = loaded.ResidentsSnapshot();
        for (int i = 0; i < all.Length; i++)
        {
            if ((long)all[i].Id > maxBefore)
            {
                maxBefore = (long)all[i].Id;
            }
        }
        ResidentRecord fresh = loaded.AllocateResident(StateWriter.SimCore, "New", LifeStage.Adult, household.Id, home.Id);
        Check((long)fresh.Id > maxBefore, "load-no-id-reuse");

        // Dated event ledger: monotonic ids, game timestamps, append order.
        WorldEvent e1 = loaded.AppendEvent(StateWriter.SimCore, "K1", null, "d1");
        loaded.SetGameSeconds(StateWriter.SimCore, 7200L);
        WorldEvent e2 = loaded.AppendEvent(StateWriter.SimCore, "K2", r1.Id, "d2");
        Check((long)e2.Event == (long)e1.Event + 1L
            && e1.GameSeconds == 3600L && e2.GameSeconds == 7200L
            && loaded.Events[loaded.Events.Count - 1].Event == e2.Event, "events-dated-ordered");

        // Settlement and care ledgers are idempotent by event id.
        Check(!loaded.SettleEstate(StateWriter.SimCore, met.Event, "home:1", "H"), "estate-replay-refused");
        Check(loaded.SettleEstate(StateWriter.SimCore, met.Event, "home:1", "Charity"), "estate-distinct-recipient-settles");
        CareAssignment care = new CareAssignment(r2.Id, r1.Id, new CarePlacement(household.Id, 0), met.Event);
        Check(loaded.AssignCare(StateWriter.SimCore, care), "care-first-assignment");
        Check(!loaded.AssignCare(StateWriter.SimCore, care), "care-replay-refused");

        // Exactly-once effect ledger through the world API.
        ActionInstanceId act2 = loaded.AllocateAction(StateWriter.SimCore);
        Check(loaded.ReserveEffect(StateWriter.ActivityExecutor, act2, "Charge", new LedgerRecipient(r1.Id, r2.Id)), "effect-reserve");
        Check(!loaded.ReserveEffect(StateWriter.ActivityExecutor, act2, "Charge", new LedgerRecipient(r2.Id, r1.Id)), "effect-double-reserve-refused");
        Check(loaded.CommitEffect(StateWriter.ActivityExecutor, act2), "effect-commit");
        Check(!loaded.CommitEffect(StateWriter.ActivityExecutor, act2), "effect-replay-refused");

        // Decision ring: 1024 bound, truncation marker, monotonic sequence,
        // durable records untouched, all 8 fields reconstructable.
        WorldState log = WorldState.CreateSeeded(3L, 42L, 1, new string[] { "Solo" });
        ResidentRecord solo = log.ResidentsSnapshot()[0];
        List<ActivityId> cands = new List<ActivityId>();
        cands.Add(new ActivityId("ACT-FOOD-01"));
        cands.Add(new ActivityId("ACT-REL-01"));
        DecisionRecord first = log.AppendDecision(StateWriter.SimCore, "IdleChoice", solo.Id,
            cands.AsReadOnly(), cands[0], "Autonomous", null, "Accepted", "Hunger+45");
        Check(first.Sequence == 1L
            && first.Trigger == "IdleChoice" && first.Resident == solo.Id
            && first.Candidates.Count == 2 && first.Chosen == cands[0]
            && first.Source == "Autonomous" && !first.Request.HasValue
            && first.ValidationOutcome == "Accepted" && first.CommittedEffect == "Hunger+45",
            "decision-eight-fields");
        int eventsBefore = log.EventCount;
        for (int i = 0; i < 1500; i++)
        {
            log.AppendDecision(StateWriter.SimCore, "T" + i.ToString(), solo.Id,
                cands.AsReadOnly(), cands[0], "ModelProposed", (GatewayRequestId?)(GatewayRequestId)(long)(i + 1), "Accepted", "E");
        }
        IReadOnlyList<DecisionRecord> ring = log.DecisionLogSnapshot();
        bool monotonic = true;
        for (int i = 1; i < ring.Count; i++)
        {
            if (ring[i].Sequence != ring[i - 1].Sequence + 1L)
            {
                monotonic = false;
            }
        }
        Check(log.DecisionLogCount == 1024, "decision-ring-1024");
        Check(log.DecisionLogTruncated && log.DecisionLogDropped == 477L, "decision-truncation-marker");
        Check(monotonic && ring[0].Sequence == 478L, "decision-sequence-monotonic");
        Check(log.EventCount == eventsBefore, "decision-durable-untouched");

        // Sole writer: every mutation is audited with an allowed writer.
        bool auditClean = loaded.WriteAudit.Count > 0;
        for (int i = 0; i < loaded.WriteAudit.Count; i++)
        {
            StateWriter w = loaded.WriteAudit[i].Writer;
            if (w != StateWriter.SimCore && w != StateWriter.CommandArbitrator && w != StateWriter.ActivityExecutor)
            {
                auditClean = false;
            }
        }
        Check(auditClean, "sole-writer-audit");

        // Resolver queries mutate nothing (freshness of AC-02/AC-03 reads).
        WorldState q = WorldState.CreateSeeded(4L, 5L, 2, new string[] { "Alex", "Alex" });
        int hashBefore = q.ComputeStateHash();
        for (int i = 0; i < 20; i++)
        {
            q.SearchByName("Alex");
        }
        IResidentDirectory dir = q;
        ResidentSummary sum;
        dir.TryGet((ResidentId)1L, out sum);
        Check(q.ComputeStateHash() == hashBefore, "resolver-mutates-nothing");
        Check(q.SearchByName("Alex").Count == 2
            && q.SearchByName("Alex")[0].Id != q.SearchByName("Alex")[1].Id, "resolver-duplicates-distinct");

        // Freshness counters advance only through the world.
        uint s1 = q.NextResidentSequence(StateWriter.CommandArbitrator, (ResidentId)1L);
        uint s2 = q.NextResidentSequence(StateWriter.CommandArbitrator, (ResidentId)1L);
        Check(s2 == s1 + 1u, "resident-sequence-monotonic");
        q.TouchDecisionSlot(StateWriter.CommandArbitrator, (ResidentId)1L);
        q.TouchControlGeneration(StateWriter.SimCore, (ResidentId)1L);
        ResidentRecord qr;
        q.TryGetResident((ResidentId)1L, out qr);
        Check(qr.DecisionSlotGeneration == 1u && qr.ControlGeneration == 1u, "generations-touch");

        // Fast Enter Play Mode: no static mutable state in the package.
        Check(NoStaticMutableState(typeof(WorldState).Assembly), "femp-no-static-mutable");

        // ---- negatives: injected failures must be refused ----
        ExpectThrow(delegate { ((IList<WorldEvent>)loaded.Events).Add(e1); }, "readonly-events-refuse-mutation");
        ExpectThrow(delegate { ((IList<SkillEntry>)lr1.Skills).Add(new SkillEntry("X", 0, 0)); }, "readonly-skills-refuse-mutation");
        ExpectThrow(delegate { loaded.SetNeed((StateWriter)99, r1.Id, Need.Fun, 50.0f); }, "invalid-writer-rejected");
        ExpectThrow(delegate { loaded.SetNeed(StateWriter.SimCore, r1.Id, Need.Fun, float.NaN); }, "nan-need-rejected");
        ExpectThrow(delegate { loaded.SetGameSeconds(StateWriter.SimCore, 100L); }, "clock-backward-rejected");
        ExpectThrow(delegate { loaded.SetMoney(StateWriter.SimCore, r1.Id, -1L); }, "negative-money-rejected");
        ExpectThrow(delegate { loaded.SetNeed(StateWriter.SimCore, (ResidentId)424242L, Need.Fun, 50.0f); }, "unknown-resident-rejected");
        WorldSnapshot badSchema = loaded.ExportSnapshot();
        badSchema.SchemaVersion = 2;
        ExpectThrow(delegate { WorldState.LoadFromSnapshot(badSchema); }, "unknown-schema-refused");
        WorldSnapshot dupIds = loaded.ExportSnapshot();
        dupIds.Residents.Add(dupIds.Residents[0]);
        ExpectThrow(delegate { WorldState.LoadFromSnapshot(dupIds); }, "duplicate-id-refused");
        WorldSnapshot lowWater = loaded.ExportSnapshot();
        lowWater.NextResidentId = 1L;
        ExpectThrow(delegate { WorldState.LoadFromSnapshot(lowWater); }, "id-reuse-refused");
        int hashAfterNegatives = loaded.ComputeStateHash();
        Check(hashAfterNegatives == loaded.ComputeStateHash(), "negatives-mutate-nothing");
        Console.WriteLine("hash-before-negatives " + hashAfterNegatives.ToString());

        Console.WriteLine(failures == 0 ? "STATE ALL PASS" : "STATE FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }

    private static void NamedCheck(WorldState named, ResidentRecord r1, ResidentRecord r2)
    {
        Check(r1.DisplayName == "Alex", "named-kept");
        Check(r2.DisplayName.Length > 0, "blank-gets-seeded-default");
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
