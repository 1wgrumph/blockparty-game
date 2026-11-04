// PROGRESSION@1: completed-activity advancement (BP-ECO-04, BP-SEC-05).
// Consumes STATE@1 (WorldState, committed effect ledger) and FOOD@1 (Food
// stays the only Cooking granter; this service never grants Cooking).
// Plain C# 9, no UnityEngine dependency.
// Tick applies each committed catalog completion once: ACT-WORK-01 grants
// +20 career points per 100-point level, ACT-EDU-01 grants school +20
// (child) or +25 (teen) education, and the eighth distinct ACT-EDU-03
// session completes CulinaryCertificate, which unlocks Barista 3+ (Food
// enforces the Cooking cap from the same certificate). Cancelled, failed
// and in-progress actions never commit, so they grant nothing; idle time
// alone grants nothing. Applied markers are dated world events (persisted
// by the existing SAVE codec), so a reconstructed service over a loaded
// world replays nothing.
// Fast Enter Play Mode: no static mutable state (consts only), no lambdas,
// no LINQ, explicit numeric conversions, no wall-clock reads.
using System;
using System.Collections.Generic;
using System.Globalization;
using Blockparty.Contracts;

namespace Blockparty.Simulation
{
    public sealed class Progression
    {
        private const string WorkCode = "ACT-WORK-01";
        private const string SchoolCode = "ACT-EDU-01";
        private const string CourseCode = "ACT-EDU-03";
        private const string Certificate = "CulinaryCertificate";
        private const string BaristaJob = "Barista";
        private const string AppliedKind = "ProgressionApplied";
        private const string ActionPrefix = "action=";
        private const int CareerGain = 20;
        private const int CareerThreshold = 100;
        private const int CourseSessions = 8;

        private readonly WorldState world;

        public Progression(WorldState world)
        {
            if (world == null)
            {
                throw new ArgumentNullException("world");
            }
            this.world = world;
        }

        // Applies every newly committed completion once, then marks it with
        // a dated event. Reserved (in-progress, cancelled, failed) entries
        // are skipped; a later tick picks up whatever commits next.
        public void Tick(StateWriter writer)
        {
            CheckWriter(writer);
            Dictionary<long, bool> applied = AppliedActions();
            IReadOnlyList<EffectLedgerEntry> ledger = world.EffectLedger;
            for (int i = 0; i < ledger.Count; i++)
            {
                EffectLedgerEntry entry = ledger[i];
                if (entry.State != LedgerState.Committed)
                {
                    continue;
                }
                long action = (long)entry.Action;
                if (applied.ContainsKey(action))
                {
                    continue;
                }
                Apply(writer, entry);
                world.AppendEvent(writer, AppliedKind, entry.Recipient.First,
                    ActionPrefix + action.ToString(CultureInfo.InvariantCulture)
                    + ";activity=" + entry.EffectKind
                    + ";resident=" + ((long)entry.Recipient.First).ToString(CultureInfo.InvariantCulture));
                applied.Add(action, true);
            }
        }

        private void Apply(StateWriter writer, EffectLedgerEntry entry)
        {
            ResidentRecord record;
            if (!world.TryGetResident(entry.Recipient.First, out record) || !record.IsAlive)
            {
                return;
            }
            if (string.Equals(entry.EffectKind, WorkCode, StringComparison.Ordinal))
            {
                ApplyCareer(writer, record);
            }
            else if (string.Equals(entry.EffectKind, SchoolCode, StringComparison.Ordinal))
            {
                ApplySchool(writer, record);
            }
            else if (string.Equals(entry.EffectKind, CourseCode, StringComparison.Ordinal))
            {
                ApplyCourse(writer, record);
            }
        }

        private void ApplyCareer(StateWriter writer, ResidentRecord record)
        {
            bool certified = HasCertificate(record);
            int level = record.CareerLevel;
            int points = record.CareerPoints + CareerGain;
            while (points >= CareerThreshold && level < StateSchema.CareerLevelMax)
            {
                if (level + 1 >= 3 && !certified
                    && string.Equals(record.JobId, BaristaJob, StringComparison.Ordinal))
                {
                    break;
                }
                level++;
                points -= CareerThreshold;
            }
            world.SetCareer(writer, record.Id, record.JobId, level, points);
        }

        private void ApplySchool(StateWriter writer, ResidentRecord record)
        {
            int gain = 0;
            if (record.Stage == LifeStage.Child)
            {
                gain = 20;
            }
            else if (record.Stage == LifeStage.Teen)
            {
                gain = 25;
            }
            if (gain > 0)
            {
                world.SetEducation(writer, record.Id, record.EducationPoints + gain);
            }
        }

        private void ApplyCourse(StateWriter writer, ResidentRecord record)
        {
            // Committed course entries persist, so the count survives a
            // save/load without any service-side session table.
            int sessions = 0;
            IReadOnlyList<EffectLedgerEntry> ledger = world.EffectLedger;
            for (int i = 0; i < ledger.Count; i++)
            {
                if (ledger[i].State == LedgerState.Committed
                    && ledger[i].Recipient.First == record.Id
                    && string.Equals(ledger[i].EffectKind, CourseCode, StringComparison.Ordinal))
                {
                    sessions++;
                }
            }
            if (sessions >= CourseSessions && !HasCertificate(record))
            {
                world.AddCompletedCourse(writer, record.Id, Certificate);
            }
        }

        private static bool HasCertificate(ResidentRecord record)
        {
            IReadOnlyList<string> courses = record.CompletedCourses;
            for (int i = 0; i < courses.Count; i++)
            {
                if (string.Equals(courses[i], Certificate, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private Dictionary<long, bool> AppliedActions()
        {
            Dictionary<long, bool> applied = new Dictionary<long, bool>();
            IReadOnlyList<WorldEvent> events = world.Events;
            for (int i = 0; i < events.Count; i++)
            {
                if (!string.Equals(events[i].Kind, AppliedKind, StringComparison.Ordinal))
                {
                    continue;
                }
                long action;
                if (TryParseAction(events[i].Detail, out action) && !applied.ContainsKey(action))
                {
                    applied.Add(action, true);
                }
            }
            return applied;
        }

        private static bool TryParseAction(string detail, out long action)
        {
            action = 0L;
            if (detail == null || !detail.StartsWith(ActionPrefix, StringComparison.Ordinal))
            {
                return false;
            }
            int end = detail.IndexOf(';');
            string digits = end < 0
                ? detail.Substring(ActionPrefix.Length)
                : detail.Substring(ActionPrefix.Length, end - ActionPrefix.Length);
            if (digits.Length == 0 || digits.Length > 19)
            {
                return false;
            }
            long value = 0L;
            for (int i = 0; i < digits.Length; i++)
            {
                if (digits[i] < '0' || digits[i] > '9')
                {
                    return false;
                }
                int digit = (int)(digits[i] - '0');
                if (value > (long.MaxValue - (long)digit) / 10L)
                {
                    return false;
                }
                value = value * 10L + (long)digit;
            }
            action = value;
            return true;
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
