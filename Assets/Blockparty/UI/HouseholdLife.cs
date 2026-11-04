// UI-LIFE@1: household, family, will and ownership controls over ordinary
// simulation commands. Consumes HOUSING@1, BIRTH@1, COMMAND@1, ESTATE@1 and
// SHARES@1; plain C# 9, no UnityEngine dependency so rendered views stay in
// CONTROLS-PLAY. Reads return detached projections and never mutate
// authority; every control delegates to Housing, Ownership, FamilyCreation
// or CommandArbitrator instead of writing world state itself.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;

namespace Blockparty.UI
{
    // Detached projection of one resident's household entry. Members, will,
    // care and ownership are copied, so later authority edits never reach an
    // already issued snapshot.
    public sealed class HouseholdLifeSnapshot
    {
        public ResidentId Resident { get; private set; }
        public HouseholdId Household { get; private set; }
        public HomeId Home { get; private set; }
        public IReadOnlyList<ResidentId> Members { get; private set; }
        public WillRecord Will { get; private set; }
        public PregnancyRecord? PendingDelivery { get; private set; }
        public IReadOnlyList<CareAssignment> Care { get; private set; }
        public OwnershipAgreement Ownership { get; private set; }

        public HouseholdLifeSnapshot(ResidentId resident, HouseholdId household, HomeId home,
            IReadOnlyList<ResidentId> members, WillRecord will, PregnancyRecord? pendingDelivery,
            IReadOnlyList<CareAssignment> care, OwnershipAgreement ownership)
        {
            if (members == null) throw new ArgumentNullException("members");
            if (care == null) throw new ArgumentNullException("care");
            Resident = resident;
            Household = household;
            Home = home;
            Members = members;
            Will = will;
            PendingDelivery = pendingDelivery;
            Care = care;
            Ownership = ownership;
        }
    }

    public sealed class HouseholdLife
    {
        private readonly GameStack game;

        public string Message { get; private set; }

        public HouseholdLife(GameStack game)
        {
            if (game == null) throw new ArgumentNullException("game");
            if (game.Housing == null) throw new ArgumentNullException("game");
            if (game.Ownership == null) throw new ArgumentNullException("game");
            this.game = game;
            Message = string.Empty;
        }

        // Fresh read: prices, capacity and blocked reasons come from Housing's
        // live offer so a later balance or household edit is visible.
        public HousingOffer HomeOffer(HouseholdId household, HomeId home, AccountRef funding)
        {
            return game.Housing.GetOffer(household, home, funding);
        }

        public HouseholdLifeSnapshot Inspect(ResidentId resident)
        {
            ResidentRecord record;
            if (!game.World.TryGetResident(resident, out record))
            {
                throw new ArgumentException("Unknown resident " + ((long)resident).ToString(), "resident");
            }
            List<ResidentId> members = new List<ResidentId>();
            ResidentRecord[] all = game.World.ResidentsSnapshot();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Household == record.Household) members.Add(all[i].Id);
            }
            WillRecord will;
            if (!game.Arbitrator.TryGetLastWill(record.Id, out will)) will = null;
            PregnancyRecord? pending = null;
            PregnancyRecord pregnancy;
            if (game.Family.TryGetPregnancy(record.Id, out pregnancy)) pending = pregnancy;
            List<CareAssignment> care = new List<CareAssignment>();
            IReadOnlyList<CareAssignment> assignments = game.World.CareAssignments;
            for (int i = 0; i < assignments.Count; i++)
            {
                if (assignments[i].Dependent == record.Id) care.Add(assignments[i]);
            }
            OwnershipAgreement agreement = game.Ownership.GetAgreement(record.Home);
            OwnershipAgreement ownership = agreement == null ? null
                : new OwnershipAgreement(agreement.Home, agreement.RecordedValue,
                    new List<OwnershipShare>(agreement.Shares));
            return new HouseholdLifeSnapshot(record.Id, record.Household, record.Home,
                members.AsReadOnly(), CloneWill(will), pending, care.AsReadOnly(), ownership);
        }

        // Envelope controls keep the normal command path: TryForBaby preflight
        // and arbitration for anything else.
        public CommandResult Submit(CommandEnvelope envelope)
        {
            if (envelope == null) throw new ArgumentNullException("envelope");
            CommandResult result = game.Family.Submit(envelope);
            Message = result.Accepted ? string.Empty : result.Reason.ToString();
            return result;
        }

        public CommandResult FormHousehold(string name, ResidentId[] residents, HomeId home)
        {
            return Household(game.Housing.FormHousehold(StateWriter.CommandArbitrator,
                name, residents, home));
        }

        public CommandResult JoinHousehold(ResidentId resident, HouseholdId household)
        {
            return Household(game.Housing.JoinHousehold(StateWriter.CommandArbitrator,
                resident, household));
        }

        public CommandResult LeaveHousehold(ResidentId resident, HomeId home)
        {
            return Household(game.Housing.LeaveHousehold(StateWriter.CommandArbitrator,
                resident, home));
        }

        public CommandResult MoveHousehold(HouseholdId household, HomeId home)
        {
            return Household(game.Housing.MoveHousehold(StateWriter.CommandArbitrator,
                household, home));
        }

        public CommandResult BuyHome(ActionInstanceId action, HouseholdId household,
            HomeId home, AccountRef funding)
        {
            return Household(game.Housing.BuyHome(StateWriter.CommandArbitrator,
                action, household, home, funding));
        }

        public CommandResult SetOwnership(HomeId home, long value, OwnershipShare[] shares)
        {
            CommandResult result = game.Ownership.SetAgreement(StateWriter.CommandArbitrator,
                home, value, shares);
            Message = result.Accepted ? string.Empty : result.Reason.ToString();
            return result;
        }

        private CommandResult Household(CommandResult result)
        {
            Message = result.Accepted ? string.Empty : game.Housing.Message;
            if (!result.Accepted && string.IsNullOrWhiteSpace(Message))
            {
                Message = result.Reason.ToString();
            }
            return result;
        }

        private static WillRecord CloneWill(WillRecord will)
        {
            if (will == null) return null;
            MoneyShare[] money = null;
            if (will.MoneyShares != null)
            {
                money = new MoneyShare[will.MoneyShares.Count];
                for (int i = 0; i < money.Length; i++) money[i] = will.MoneyShares[i];
            }
            ObjectId[] heirlooms = null;
            if (will.Heirlooms != null)
            {
                heirlooms = new ObjectId[will.Heirlooms.Count];
                for (int i = 0; i < heirlooms.Length; i++) heirlooms[i] = will.Heirlooms[i];
            }
            HouseholdId[] households = null;
            if (will.CareHouseholds != null)
            {
                households = new HouseholdId[will.CareHouseholds.Count];
                for (int i = 0; i < households.Length; i++) households[i] = will.CareHouseholds[i];
            }
            return new WillRecord(money, will.HomeSharePercent, heirlooms,
                will.Note, will.FuneralPreference, households);
        }
    }
}
