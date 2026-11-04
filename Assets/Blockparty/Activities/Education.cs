// EDUCATION@1: course sessions, in-block school and skill/career choices
// (BP-ACT-05, BP-SEC-05 ACT-EDU-01/03/04). Consumes STATE (effect ledger,
// education points, completed courses), COMMAND, NAV slot registration,
// BLOCK-SITES school desks and ACCOUNTS. PROGRESSION alone grants school
// +20/+25 and the eighth ACT-EDU-03 certificate; this service only charges
// each committed course session once and completes ACT-EDU-04, then derives
// caps and career choices from the persisted CompletedCourses. Tick observes
// completions and never ticks the executor or re-grants progress.
// Plain C# 9, no UnityEngine dependency; no static mutable state.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;
using Blockparty.Simulation;

namespace Blockparty.Activities
{
    public sealed class Education
    {
        private const StateWriter Writer = StateWriter.ActivityExecutor;
        private const string CulinaryCode = "ACT-EDU-03";
        private const string ComputerCode = "ACT-EDU-04";
        private const string CulinaryCertificate = "CulinaryCertificate";
        private const string ComputerSkills = "ComputerSkills";
        private const string CookingSkill = "Cooking";
        private const string LogicSkill = "Logic";
        private const string BaristaJob = "Barista";
        private const string TechnicianJob = "JuniorTechnician";
        private const string AiEngineerJob = "AIEngineer";
        private const string CourseFee = "Education";
        private const int CourseSessions = 8;
        private const int CertifiedCap = 10;
        private const int UncertifiedCap = 5;
        private const int GraduationPoints = 500;
        private const long CulinaryPrice = 40L;
        private const long ComputerPrice = 50L;

        private readonly WorldState world;
        private readonly CommandArbitrator arbitrator;
        private readonly Accounts accounts;

        public Education(WorldState world, CommandArbitrator arbitrator,
            ActivityExecutor executor, Accounts accounts, BlockSites sites)
        {
            if (world == null) throw new ArgumentNullException("world");
            if (arbitrator == null) throw new ArgumentNullException("arbitrator");
            if (executor == null) throw new ArgumentNullException("executor");
            if (accounts == null) throw new ArgumentNullException("accounts");
            if (sites == null) throw new ArgumentNullException("sites");
            this.world = world;
            this.arbitrator = arbitrator;
            this.accounts = accounts;
        }

        // Course and school commands flow through COMMAND like every other
        // direct activity; stage and target validation stay there.
        public CommandResult Submit(CommandEnvelope command)
        {
            if (command == null) throw new ArgumentNullException("command");
            return arbitrator.Submit(command);
        }

        public int SkillCap(ResidentId resident, string skill)
        {
            ResidentRecord record = Require(resident);
            if (string.Equals(skill, CookingSkill, StringComparison.Ordinal))
            {
                return HasCourse(record, CulinaryCertificate) ? CertifiedCap : UncertifiedCap;
            }
            if (string.Equals(skill, LogicSkill, StringComparison.Ordinal))
            {
                return HasCourse(record, ComputerSkills) ? CertifiedCap : UncertifiedCap;
            }
            return StateSchema.SkillLevelMax;
        }

        // Mirrors WORK's ACT-WORK entry restrictions: the certificate opens
        // Barista 3+, the course opens Technician 2+ and AIEngineer entry.
        public bool CanEnterCareer(ResidentId resident, string job, int level)
        {
            ResidentRecord record = Require(resident);
            if (level < 1 || level > StateSchema.CareerLevelMax) return false;
            if (string.Equals(job, BaristaJob, StringComparison.Ordinal))
            {
                if (record.Stage < LifeStage.Teen) return false;
                if (record.Stage == LifeStage.Teen && level > 1) return false;
                return level < 3 || HasCourse(record, CulinaryCertificate);
            }
            if (record.Stage < LifeStage.YoungAdult) return false;
            if (string.Equals(job, TechnicianJob, StringComparison.Ordinal))
            {
                return level < 2 || HasCourse(record, ComputerSkills);
            }
            if (string.Equals(job, AiEngineerJob, StringComparison.Ordinal))
            {
                return HasCourse(record, ComputerSkills);
            }
            return false;
        }

        public bool IsGraduated(ResidentId resident)
        {
            return Require(resident).EducationPoints >= GraduationPoints;
        }

        // Charges every newly committed course session through the accounts
        // ledger (Transfer is idempotent per ActionInstanceId and the applied
        // set persists), and completes ComputerSkills at eight sessions.
        // School progress and the Culinary certificate belong to PROGRESSION.
        public void Tick(StateWriter writer)
        {
            CheckWriter(writer);
            IReadOnlyList<EffectLedgerEntry> ledger = world.EffectLedger;
            for (int i = 0; i < ledger.Count; i++)
            {
                EffectLedgerEntry entry = ledger[i];
                if (entry.State != LedgerState.Committed) continue;
                long price;
                bool computer;
                if (string.Equals(entry.EffectKind, CulinaryCode, StringComparison.Ordinal))
                {
                    price = CulinaryPrice;
                    computer = false;
                }
                else if (string.Equals(entry.EffectKind, ComputerCode, StringComparison.Ordinal))
                {
                    price = ComputerPrice;
                    computer = true;
                }
                else
                {
                    continue;
                }
                ResidentRecord record;
                if (!world.TryGetResident(entry.Recipient.First, out record) || !record.IsAlive)
                {
                    continue;
                }
                accounts.Transfer(writer, entry.Action, AccountRef.Personal(record.Id),
                    AccountRef.Expense(CourseFee), price);
                if (computer && !HasCourse(record, ComputerSkills)
                    && CommittedSessions(record.Id, ComputerCode) >= CourseSessions)
                {
                    world.AddCompletedCourse(writer, record.Id, ComputerSkills);
                }
            }
        }

        private int CommittedSessions(ResidentId resident, string code)
        {
            int count = 0;
            IReadOnlyList<EffectLedgerEntry> ledger = world.EffectLedger;
            for (int i = 0; i < ledger.Count; i++)
            {
                if (ledger[i].State == LedgerState.Committed
                    && ledger[i].Recipient.First == resident
                    && string.Equals(ledger[i].EffectKind, code, StringComparison.Ordinal))
                {
                    count++;
                }
            }
            return count;
        }

        private ResidentRecord Require(ResidentId resident)
        {
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record))
            {
                throw new ArgumentException("Unknown resident " + ((long)resident).ToString(), "resident");
            }
            return record;
        }

        private static bool HasCourse(ResidentRecord record, string course)
        {
            IReadOnlyList<string> courses = record.CompletedCourses;
            for (int i = 0; i < courses.Count; i++)
            {
                if (string.Equals(courses[i], course, StringComparison.Ordinal)) return true;
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
