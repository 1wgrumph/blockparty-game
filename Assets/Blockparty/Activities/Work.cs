// WORK@1: work shifts and job search (BP-ACT-04 / BP-SEC-05).
// Consumes COMMAND (CommandArbitrator), NAV (ActivityExecutor stages/slots/
// ledger), BLOCK-SITES targets and PROGRESSION (which alone grants Barista
// ACT-WORK-01 career points from the committed ledger; this service grants
// the ACT-WORK-02/04 points so no shift is credited twice). Plain C# 9.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;
using Blockparty.Simulation;

namespace Blockparty.Activities
{
    public sealed class Work
    {
        private const StateWriter Writer = StateWriter.ActivityExecutor;
        private const string BaristaCode = "ACT-WORK-01";
        private const string TechnicianCode = "ACT-WORK-02";
        private const string JobSearchCode = "ACT-WORK-03";
        private const string AiEngineerCode = "ACT-WORK-04";

        private sealed class Tracked
        {
            public ResidentId Resident;
            public string Code;
            public bool Done;
        }

        private readonly WorldState world;
        private readonly CommandArbitrator arbitrator;
        private readonly ActivityExecutor executor;
        private readonly Accounts accounts;
        private readonly BlockSites sites;
        private readonly Dictionary<ActionInstanceId, Tracked> tracked = new Dictionary<ActionInstanceId, Tracked>();
        private long lastGameSeconds;

        public Work(WorldState world, CommandArbitrator arbitrator,
            ActivityExecutor executor, Accounts accounts, BlockSites sites)
        {
            if (world == null) throw new ArgumentNullException("world");
            if (arbitrator == null) throw new ArgumentNullException("arbitrator");
            if (executor == null) throw new ArgumentNullException("executor");
            if (accounts == null) throw new ArgumentNullException("accounts");
            if (sites == null) throw new ArgumentNullException("sites");
            this.world = world;
            this.arbitrator = arbitrator;
            this.executor = executor;
            this.accounts = accounts;
            this.sites = sites;
            lastGameSeconds = world.GameSeconds;
        }

        public IEnumerable<string> AvailableJobs(ResidentId resident, ObjectId? searchComputer)
        {
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record))
                throw new ArgumentException("Unknown resident " + ((long)resident).ToString(), "resident");
            return new string[] { "Barista", "JuniorTechnician", "AIEngineer" };
        }

        public bool Hire(ResidentId resident, string jobId)
        {
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record) || !record.IsAlive) return false;
            if (!CanHire(jobId, record)) return false;
            world.SetCareer(StateWriter.CommandArbitrator, resident, jobId, 1, 0);
            return true;
        }

        public bool Quit(ResidentId resident)
        {
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record) || !record.IsAlive) return false;
            world.SetCareer(StateWriter.CommandArbitrator, resident, string.Empty, 1, 0);
            return true;
        }

        public static bool CanHire(string jobId, ResidentRecord record)
        {
            if (record == null) return false;
            if (string.Equals(jobId, "Barista", StringComparison.Ordinal))
            {
                return record.Stage >= LifeStage.Teen;
            }
            if (string.Equals(jobId, "JuniorTechnician", StringComparison.Ordinal)
                || string.Equals(jobId, "Technician", StringComparison.Ordinal))
            {
                return record.Stage >= LifeStage.YoungAdult;
            }
            if (string.Equals(jobId, "AIEngineer", StringComparison.Ordinal))
            {
                return record.Stage >= LifeStage.YoungAdult && HasCourse(record, "ComputerSkills");
            }
            return false;
        }

        public CommandResult Submit(CommandEnvelope command)
        {
            if (command == null) throw new ArgumentNullException("command");
            string code = WorkCode(command);
            if (code != null && command.Resident.HasValue)
            {
                ResidentRecord record;
                if (world.TryGetResident(command.Resident.Value, out record))
                {
                    CommandResult refused = Refuse(code, record);
                    if (!refused.Accepted) return refused;
                }
            }
            CommandResult result = arbitrator.Submit(command);
            if (result.Accepted && code != null && command.Resident.HasValue)
            {
                Track(command.Resident.Value);
                ActionEntry current = arbitrator.GetCurrent(command.Resident.Value);
                if (current != null && IsWorkCode(current.Activity.Code))
                    executor.Dispatch(command.Resident.Value, current);
            }
            return result;
        }

        public void Tick(StateWriter writer)
        {
            CheckWriter(writer);
            Sweep();
            // Drive one executor tick per simulation tick elapsed since the
            // last call, so travel speed stays bound to game time.
            long now = world.GameSeconds;
            long steps = now > lastGameSeconds ? (now - lastGameSeconds) / SimTick.TickGameSeconds : 0L;
            lastGameSeconds = now;
            for (long i = 0L; i < steps; i++) executor.Tick(StateWriter.ActivityExecutor);
            foreach (KeyValuePair<ActionInstanceId, Tracked> pair in tracked)
            {
                Tracked entry = pair.Value;
                if (entry.Done) continue;
                ExecutionRecord execution = executor.GetExecution(pair.Key);
                if (execution == null || !execution.IsTerminal) continue;
                entry.Done = true;
                ResidentRecord record;
                if (execution.Outcome == TerminalOutcome.Completed
                    && world.TryGetResident(entry.Resident, out record) && record.IsAlive)
                    Apply(entry, record);
            }
        }

        // Completion is applied by Tick on natural completion only; a
        // notification is never authority to pay again.
        public CommandResult Complete(ResidentId resident, ActionInstanceId action)
        {
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record))
                throw new ArgumentException("Unknown resident " + ((long)resident).ToString(), "resident");
            return CommandResult.Reject(RejectReason.InvalidParameters);
        }

        private void Apply(Tracked entry, ResidentRecord record)
        {
            long hourly; long hours; int energy; int fun; int gain;
            if (entry.Code == BaristaCode) { hourly = 14L; hours = 6L; energy = 30; fun = 10; gain = 0; }
            else if (entry.Code == TechnicianCode) { hourly = 20L; hours = 8L; energy = 40; fun = 15; gain = 25; }
            else if (entry.Code == AiEngineerCode) { hourly = 24L; hours = 8L; energy = 40; fun = 15; gain = 25; }
            else return; // JobSearch: no pay, needs or progress.
            int level = record.CareerLevel;
            long pay = hours * hourly * (100L + 15L * (long)(level - 1)) / 100L;
            world.SetMoney(Writer, record.Id, record.Money + pay);
            world.SetNeed(Writer, record.Id, Need.Energy, record.GetNeed(Need.Energy) - (float)energy);
            world.SetNeed(Writer, record.Id, Need.Fun, record.GetNeed(Need.Fun) - (float)fun);
            if (gain > 0)
            {
                int points = record.CareerPoints + gain;
                while (points >= 100 && level < StateSchema.CareerLevelMax) { level++; points -= 100; }
                world.SetCareer(Writer, record.Id, record.JobId, level, points);
            }
        }

        private CommandResult Refuse(string code, ResidentRecord record)
        {
            if (code == JobSearchCode) return CommandResult.Accept();
            if (code == BaristaCode)
            {
                if (record.Stage < LifeStage.Teen
                    || (record.Stage == LifeStage.Teen && record.CareerLevel > 1))
                    return CommandResult.Reject(RejectReason.StageIneligible);
                if (record.CareerLevel >= 3 && !HasCourse(record, "CulinaryCertificate"))
                    return CommandResult.Reject(RejectReason.StageIneligible);
                return CommandResult.Accept();
            }
            if (record.Stage < LifeStage.YoungAdult)
                return CommandResult.Reject(RejectReason.StageIneligible);
            if (code == TechnicianCode && record.CareerLevel >= 2 && !HasCourse(record, "ComputerSkills"))
                return CommandResult.Reject(RejectReason.StageIneligible);
            if (code == AiEngineerCode && !HasCourse(record, "ComputerSkills"))
                return CommandResult.Reject(RejectReason.StageIneligible);
            return CommandResult.Accept();
        }

        private static bool HasCourse(ResidentRecord record, string course)
        {
            for (int i = 0; i < record.CompletedCourses.Count; i++)
                if (string.Equals(record.CompletedCourses[i], course, StringComparison.Ordinal)) return true;
            return false;
        }

        private void Sweep()
        {
            ResidentRecord[] residents = world.ResidentsSnapshot();
            for (int i = 0; i < residents.Length; i++)
                if (residents[i].IsAlive) Track(residents[i].Id);
        }

        private void Track(ResidentId resident)
        {
            ActionEntry current = arbitrator.GetCurrent(resident);
            if (current != null) TrackEntry(resident, current);
            ActionEntry[] pending = arbitrator.GetPending(resident);
            for (int i = 0; i < pending.Length; i++) TrackEntry(resident, pending[i]);
        }

        private void TrackEntry(ResidentId resident, ActionEntry entry)
        {
            if (entry.Kind != CommandKind.DirectActivity || !IsWorkCode(entry.Activity.Code)) return;
            if (tracked.ContainsKey(entry.Action)) return;
            Tracked next = new Tracked();
            next.Resident = resident;
            next.Code = entry.Activity.Code;
            tracked.Add(entry.Action, next);
        }

        private static string WorkCode(CommandEnvelope command)
        {
            if (command.Kind != CommandKind.DirectActivity || !command.Activity.HasValue) return null;
            return IsWorkCode(command.Activity.Value.Code) ? command.Activity.Value.Code : null;
        }

        private static bool IsWorkCode(string code)
        {
            return code == BaristaCode || code == TechnicianCode || code == JobSearchCode || code == AiEngineerCode;
        }

        private static void CheckWriter(StateWriter writer)
        {
            if (writer != StateWriter.SimCore && writer != StateWriter.CommandArbitrator && writer != StateWriter.ActivityExecutor)
                throw new ArgumentException("Unknown state writer", "writer");
        }
    }
}
