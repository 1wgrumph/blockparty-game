// BIRTH@1: ordinary TryForBaby, gestation, delivery and inherited appearance.
// Uses the shared command, executor, clock, housing and appearance services;
// it never invents an alternate resident-creation or effect path. Plain C# 9.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;

namespace Blockparty.Simulation
{
    // Persisted birth/inheritance receipt. Parent and child appearance rows are
    // snapshots taken at delivery; later edits never rewrite this receipt.
    public sealed class BirthRecord
    {
        public ResidentId Child { get; private set; }
        public ResidentId Carrier { get; private set; }
        public ResidentId OtherParent { get; private set; }
        public EventId ConceptionEvent { get; private set; }
        public EventId BirthEvent { get; private set; }
        public long WorldSeed { get; private set; }
        public long BaseBodySeed { get; private set; }
        public BaseBody BaseBody { get; private set; }
        public AppearanceRecord CarrierAppearance { get; private set; }
        public AppearanceRecord OtherParentAppearance { get; private set; }
        public AppearanceRecord ChildAppearance { get; private set; }

        public BirthRecord(ResidentId child, ResidentId carrier, ResidentId otherParent,
            EventId conceptionEvent, EventId birthEvent, long worldSeed, long baseBodySeed,
            BaseBody baseBody, AppearanceRecord carrierAppearance,
            AppearanceRecord otherParentAppearance, AppearanceRecord childAppearance)
        {
            Child = child;
            Carrier = carrier;
            OtherParent = otherParent;
            ConceptionEvent = conceptionEvent;
            BirthEvent = birthEvent;
            WorldSeed = worldSeed;
            BaseBodySeed = baseBodySeed;
            BaseBody = baseBody;
            CarrierAppearance = carrierAppearance;
            OtherParentAppearance = otherParentAppearance;
            ChildAppearance = childAppearance;
        }
    }

    public sealed class FamilyCreation
    {
        private const string TryForBabyCode = "ACT-REL-06";
        private const double ConceptionChance = 0.8;
        private const long DaySeconds = 86400L;
        private const long BaseBodyStreamStep = 7919L;

        public sealed class FamilySnapshot
        {
            public sealed class CapabilityRow { public long Resident; public bool CanCarry; public bool CanImpregnate; }
            public sealed class PartnerRow { public long Initiator; public long Partner; public bool Consent; }
            public sealed class BedRow { public long Object; public long Home; }
            public sealed class PregnancyRow { public long Carrier; public long OtherParent; public long ConceptionEvent; public long DueGameTime; public int CapacityHold; public long Household; }
            public sealed class BirthRow
            {
                public long Child; public long Carrier; public long OtherParent;
                public long ConceptionEvent; public long BirthEvent;
                public long WorldSeed; public long BaseBodySeed; public int BaseBody;
                public AppearanceEntry CarrierAppearance;
                public AppearanceEntry OtherParentAppearance;
                public AppearanceEntry ChildAppearance;
            }

            public readonly List<CapabilityRow> Capabilities = new List<CapabilityRow>();
            public readonly List<PartnerRow> Partners = new List<PartnerRow>();
            public readonly List<BedRow> Beds = new List<BedRow>();
            public readonly List<PregnancyRow> Pregnancies = new List<PregnancyRow>();
            public readonly List<BirthRow> Births = new List<BirthRow>();
        }

        private sealed class Capability
        {
            public bool CanCarry;
            public bool CanImpregnate;
        }

        private sealed class PartnerLink
        {
            public ResidentId Partner;
            public bool Consent;
        }

        // Contract PregnancyRecord plus the household whose newborn place it
        // reserves; the contract row carries no household field.
        private sealed class PregnancyState
        {
            public PregnancyRecord Record;
            public HouseholdId Household;
        }

        private readonly WorldState world;
        private readonly CommandArbitrator arbitrator;
        private readonly ActivityExecutor executor;
        private readonly AppearanceStore appearances;
        private readonly Housing housing;
        private readonly Dictionary<ResidentId, Capability> capabilities = new Dictionary<ResidentId, Capability>();
        private readonly Dictionary<ResidentId, PartnerLink> partners = new Dictionary<ResidentId, PartnerLink>();
        private readonly Dictionary<ObjectId, HomeId> beds = new Dictionary<ObjectId, HomeId>();
        private readonly Dictionary<ResidentId, PregnancyState> pregnancies = new Dictionary<ResidentId, PregnancyState>();
        private readonly Dictionary<ResidentId, BirthRecord> births = new Dictionary<ResidentId, BirthRecord>();
        private readonly Dictionary<ResidentId, ActionInstanceId> pendingConceptions = new Dictionary<ResidentId, ActionInstanceId>();

        public FamilyCreation(WorldState world, CommandArbitrator arbitrator,
            ActivityExecutor executor, AppearanceStore appearances, Housing housing)
        {
            if (world == null) throw new ArgumentNullException("world");
            if (arbitrator == null) throw new ArgumentNullException("arbitrator");
            if (executor == null) throw new ArgumentNullException("executor");
            if (appearances == null) throw new ArgumentNullException("appearances");
            this.world = world;
            this.arbitrator = arbitrator;
            this.executor = executor;
            this.appearances = appearances;
            this.housing = housing;
        }

        public bool HasSaveState()
        {
            return capabilities.Count > 0 || partners.Count > 0 || beds.Count > 0
                || pregnancies.Count > 0 || births.Count > 0;
        }

        public void SetReproductiveCapabilities(StateWriter writer, ResidentId id, bool canCarry, bool canImpregnate)
        {
            CheckWriter(writer, "SetReproductiveCapabilities");
            RequireResident(id);
            Capability cap = new Capability();
            cap.CanCarry = canCarry;
            cap.CanImpregnate = canImpregnate;
            capabilities[id] = cap;
        }

        public void SetPartner(StateWriter writer, ResidentId initiator, ResidentId partner, bool consent)
        {
            CheckWriter(writer, "SetPartner");
            RequireResident(initiator);
            RequireResident(partner);
            if (initiator == partner)
            {
                throw new ArgumentException("A resident cannot be their own partner", "partner");
            }
            PartnerLink link = new PartnerLink();
            link.Partner = partner;
            link.Consent = consent;
            partners[initiator] = link;
        }

        public void RegisterBed(StateWriter writer, ObjectId bed, HomeId home)
        {
            CheckWriter(writer, "RegisterBed");
            if ((long)bed <= 0L || (long)bed >= world.NextObjectId)
            {
                throw new ArgumentException("Unknown object " + ((long)bed).ToString(), "bed");
            }
            if (!HomeExists(home))
            {
                throw new ArgumentException("Unknown home " + ((long)home).ToString(), "home");
            }
            beds[bed] = home;
        }

        public CommandResult Submit(CommandEnvelope envelope)
        {
            if (envelope == null) throw new ArgumentNullException("envelope");
            if (envelope.Kind != CommandKind.DirectActivity
                || !envelope.Activity.HasValue
                || !string.Equals(envelope.Activity.Value.Code, TryForBabyCode, StringComparison.Ordinal))
            {
                return arbitrator.Submit(envelope);
            }
            if (!envelope.Resident.HasValue || !envelope.TargetId.HasValue)
            {
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            ResidentId carrier = envelope.Resident.Value;
            ResidentRecord carrierRecord;
            if (!world.TryGetResident(carrier, out carrierRecord) || !carrierRecord.IsAlive)
            {
                return CommandResult.Reject(RejectReason.ResidentDeceased);
            }
            if (carrierRecord.Stage != LifeStage.YoungAdult && carrierRecord.Stage != LifeStage.Adult)
            {
                return CommandResult.Reject(RejectReason.StageIneligible);
            }
            Capability carrierCapability;
            if (!capabilities.TryGetValue(carrier, out carrierCapability) || !carrierCapability.CanCarry)
            {
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            PartnerLink link;
            if (!partners.TryGetValue(carrier, out link) || !link.Consent)
            {
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            ResidentRecord otherRecord;
            if (!world.TryGetResident(link.Partner, out otherRecord) || !otherRecord.IsAlive
                || otherRecord.Stage < LifeStage.YoungAdult)
            {
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            Capability otherCapability;
            if (!capabilities.TryGetValue(otherRecord.Id, out otherCapability) || !otherCapability.CanImpregnate)
            {
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            if (pregnancies.ContainsKey(carrier))
            {
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            HomeId bedHome;
            if (!beds.TryGetValue((ObjectId)envelope.TargetId.Value, out bedHome) || bedHome != carrierRecord.Home)
            {
                return CommandResult.Reject(RejectReason.TargetMissing);
            }
            if (!BedAccepted(carrierRecord, otherRecord))
            {
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            CommandResult result = arbitrator.Submit(envelope);
            if (result.Accepted)
            {
                ActionEntry entry = FindAction(carrier, TryForBabyCode);
                if (entry != null)
                {
                    pendingConceptions[carrier] = entry.Action;
                }
            }
            return result;
        }

        public void Tick(StateWriter writer)
        {
            CheckWriter(writer, "Tick");
            // Executor commits carry their own audited ActivityExecutor identity.
            executor.Tick(StateWriter.ActivityExecutor);
            ProcessCompletedConceptions(writer);
            ProcessPregnancies(writer);
        }

        public bool TryGetPregnancy(ResidentId carrier, out PregnancyRecord pregnancy)
        {
            PregnancyState found;
            if (pregnancies.TryGetValue(carrier, out found))
            {
                pregnancy = found.Record;
                return true;
            }
            pregnancy = default(PregnancyRecord);
            return false;
        }

        public BirthRecord GetBirth(ResidentId child)
        {
            BirthRecord found;
            return births.TryGetValue(child, out found) ? found : null;
        }

        public FamilySnapshot Export()
        {
            FamilySnapshot snapshot = new FamilySnapshot();
            List<ResidentId> ids = new List<ResidentId>(capabilities.Keys);
            ids.Sort(delegate (ResidentId x, ResidentId y) { return ((long)x).CompareTo((long)y); });
            for (int i = 0; i < ids.Count; i++)
            {
                FamilySnapshot.CapabilityRow row = new FamilySnapshot.CapabilityRow();
                row.Resident = (long)ids[i];
                row.CanCarry = capabilities[ids[i]].CanCarry;
                row.CanImpregnate = capabilities[ids[i]].CanImpregnate;
                snapshot.Capabilities.Add(row);
            }
            List<ResidentId> partnerIds = new List<ResidentId>(partners.Keys);
            partnerIds.Sort(delegate (ResidentId x, ResidentId y) { return ((long)x).CompareTo((long)y); });
            for (int i = 0; i < partnerIds.Count; i++)
            {
                PartnerLink link = partners[partnerIds[i]];
                FamilySnapshot.PartnerRow row = new FamilySnapshot.PartnerRow();
                row.Initiator = (long)partnerIds[i];
                row.Partner = (long)link.Partner;
                row.Consent = link.Consent;
                snapshot.Partners.Add(row);
            }
            List<ObjectId> bedIds = new List<ObjectId>(beds.Keys);
            bedIds.Sort(delegate (ObjectId x, ObjectId y) { return ((long)x).CompareTo((long)y); });
            for (int i = 0; i < bedIds.Count; i++)
            {
                FamilySnapshot.BedRow row = new FamilySnapshot.BedRow();
                row.Object = (long)bedIds[i];
                row.Home = (long)beds[bedIds[i]];
                snapshot.Beds.Add(row);
            }
            List<ResidentId> carriers = new List<ResidentId>(pregnancies.Keys);
            carriers.Sort(delegate (ResidentId x, ResidentId y) { return ((long)x).CompareTo((long)y); });
            for (int i = 0; i < carriers.Count; i++)
            {
                PregnancyState state = pregnancies[carriers[i]];
                PregnancyRecord pregnancy = state.Record;
                FamilySnapshot.PregnancyRow row = new FamilySnapshot.PregnancyRow();
                row.Carrier = (long)pregnancy.Carrier;
                row.OtherParent = (long)pregnancy.OtherParent;
                row.ConceptionEvent = (long)pregnancy.ConceptionEvent;
                row.DueGameTime = pregnancy.DueGameTime;
                row.CapacityHold = pregnancy.CapacityHold;
                row.Household = (long)state.Household;
                snapshot.Pregnancies.Add(row);
            }
            List<ResidentId> children = new List<ResidentId>(births.Keys);
            children.Sort(delegate (ResidentId x, ResidentId y) { return ((long)x).CompareTo((long)y); });
            for (int i = 0; i < children.Count; i++)
            {
                BirthRecord birth = births[children[i]];
                FamilySnapshot.BirthRow row = new FamilySnapshot.BirthRow();
                row.Child = (long)birth.Child;
                row.Carrier = (long)birth.Carrier;
                row.OtherParent = (long)birth.OtherParent;
                row.ConceptionEvent = (long)birth.ConceptionEvent;
                row.BirthEvent = (long)birth.BirthEvent;
                row.WorldSeed = birth.WorldSeed;
                row.BaseBodySeed = birth.BaseBodySeed;
                row.BaseBody = (int)birth.BaseBody;
                row.CarrierAppearance = ToEntry(birth.CarrierAppearance);
                row.OtherParentAppearance = ToEntry(birth.OtherParentAppearance);
                row.ChildAppearance = ToEntry(birth.ChildAppearance);
                snapshot.Births.Add(row);
            }
            return snapshot;
        }

        public void Import(StateWriter writer, FamilySnapshot snapshot)
        {
            CheckWriter(writer, "Import");
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            capabilities.Clear();
            partners.Clear();
            beds.Clear();
            pregnancies.Clear();
            births.Clear();
            pendingConceptions.Clear();
            for (int i = 0; i < snapshot.Capabilities.Count; i++)
            {
                FamilySnapshot.CapabilityRow row = snapshot.Capabilities[i];
                ResidentId id = (ResidentId)row.Resident;
                RequireResident(id);
                if (capabilities.ContainsKey(id))
                {
                    throw new ArgumentException("Duplicate reproductive capability for resident " + row.Resident.ToString(), "snapshot");
                }
                Capability cap = new Capability();
                cap.CanCarry = row.CanCarry;
                cap.CanImpregnate = row.CanImpregnate;
                capabilities.Add(id, cap);
            }
            for (int i = 0; i < snapshot.Partners.Count; i++)
            {
                FamilySnapshot.PartnerRow row = snapshot.Partners[i];
                ResidentId initiator = (ResidentId)row.Initiator;
                ResidentId partner = (ResidentId)row.Partner;
                RequireResident(initiator);
                RequireResident(partner);
                if (partners.ContainsKey(initiator))
                {
                    throw new ArgumentException("Duplicate partner link for resident " + row.Initiator.ToString(), "snapshot");
                }
                PartnerLink link = new PartnerLink();
                link.Partner = partner;
                link.Consent = row.Consent;
                partners.Add(initiator, link);
            }
            for (int i = 0; i < snapshot.Beds.Count; i++)
            {
                FamilySnapshot.BedRow row = snapshot.Beds[i];
                ObjectId bed = (ObjectId)row.Object;
                HomeId home = (HomeId)row.Home;
                if ((long)bed <= 0L || (long)bed >= world.NextObjectId)
                {
                    throw new ArgumentException("Unknown object " + row.Object.ToString(), "snapshot");
                }
                if (!HomeExists(home))
                {
                    throw new ArgumentException("Unknown home " + row.Home.ToString(), "snapshot");
                }
                if (beds.ContainsKey(bed))
                {
                    throw new ArgumentException("Duplicate bed " + row.Object.ToString(), "snapshot");
                }
                beds.Add(bed, home);
            }
            for (int i = 0; i < snapshot.Pregnancies.Count; i++)
            {
                FamilySnapshot.PregnancyRow row = snapshot.Pregnancies[i];
                ResidentId carrier = (ResidentId)row.Carrier;
                ResidentId other = (ResidentId)row.OtherParent;
                RequireResident(carrier);
                RequireResident(other);
                if (!HouseholdExists((HouseholdId)row.Household))
                {
                    throw new ArgumentException("Unknown household " + row.Household.ToString(), "snapshot");
                }
                if (row.CapacityHold < 0)
                {
                    throw new ArgumentException("Negative capacity hold", "snapshot");
                }
                if (pregnancies.ContainsKey(carrier))
                {
                    throw new ArgumentException("Duplicate pregnancy for carrier " + row.Carrier.ToString(), "snapshot");
                }
                PregnancyState state = new PregnancyState();
                state.Record = new PregnancyRecord(carrier, other,
                    (EventId)row.ConceptionEvent, row.DueGameTime, row.CapacityHold);
                state.Household = (HouseholdId)row.Household;
                pregnancies.Add(carrier, state);
            }
            for (int i = 0; i < snapshot.Births.Count; i++)
            {
                FamilySnapshot.BirthRow row = snapshot.Births[i];
                ResidentId child = (ResidentId)row.Child;
                ResidentRecord childRecord = RequireResident(child);
                RequireResident((ResidentId)row.Carrier);
                RequireResident((ResidentId)row.OtherParent);
                if (row.WorldSeed != world.Seed)
                {
                    throw new ArgumentException("Birth record world seed does not match this world", "snapshot");
                }
                if (row.BaseBodySeed != world.Seed + BaseBodyStreamStep * (long)child)
                {
                    throw new ArgumentException("Birth body seed is not derived from this world and child", "snapshot");
                }
                ResidentFactory.CheckBaseBodyRange(row.BaseBody);
                BaseBody drawn = (BaseBody)new WorldRng(row.BaseBodySeed).NextInt(0, 2);
                if ((int)drawn != row.BaseBody)
                {
                    throw new ArgumentException("Birth body does not match its recorded seeded draw", "snapshot");
                }
                WorldEvent conceptionEvent = RequireEvent((EventId)row.ConceptionEvent, "Conception");
                WorldEvent birthEvent = RequireEvent((EventId)row.BirthEvent, "Birth");
                if (birthEvent.Actor.HasValue && birthEvent.Actor.Value != (ResidentId)row.Carrier)
                {
                    throw new ArgumentException("Birth event actor does not match the carrier", "snapshot");
                }
                if (!HasParentLink(childRecord, (ResidentId)row.Carrier)
                    || !HasParentLink(childRecord, (ResidentId)row.OtherParent))
                {
                    throw new ArgumentException("Birth record parents do not match the child family links", "snapshot");
                }
                if ((long)conceptionEvent.Event >= (long)birthEvent.Event)
                {
                    throw new ArgumentException("Birth event must follow its conception event", "snapshot");
                }
                AppearanceEntry carrierEntry = row.CarrierAppearance;
                AppearanceEntry otherEntry = row.OtherParentAppearance;
                AppearanceEntry childEntry = row.ChildAppearance;
                if (carrierEntry == null || otherEntry == null || childEntry == null)
                {
                    throw new ArgumentException("Birth record is missing an appearance snapshot", "snapshot");
                }
                if (carrierEntry.ResidentId != row.Carrier
                    || otherEntry.ResidentId != row.OtherParent
                    || childEntry.ResidentId != row.Child)
                {
                    throw new ArgumentException("Birth record appearance identity does not match its residents", "snapshot");
                }
                if ((int)childEntry.BaseBody != row.BaseBody)
                {
                    throw new ArgumentException("Birth record body does not match the child snapshot", "snapshot");
                }
                if (births.ContainsKey(child))
                {
                    throw new ArgumentException("Duplicate birth for child " + row.Child.ToString(), "snapshot");
                }
                births.Add(child, new BirthRecord(child,
                    (ResidentId)row.Carrier, (ResidentId)row.OtherParent,
                    (EventId)row.ConceptionEvent, (EventId)row.BirthEvent,
                    row.WorldSeed, row.BaseBodySeed, (BaseBody)row.BaseBody,
                    FromEntry(carrierEntry), FromEntry(otherEntry), FromEntry(childEntry)));
            }
        }

        private void ProcessCompletedConceptions(StateWriter writer)
        {
            List<ResidentId> done = new List<ResidentId>();
            foreach (KeyValuePair<ResidentId, ActionInstanceId> pair in pendingConceptions)
            {
                ExecutionRecord record = executor.GetExecution(pair.Value);
                if (record == null || !record.IsTerminal)
                {
                    continue;
                }
                done.Add(pair.Key);
                if (record.Outcome != TerminalOutcome.Completed
                    || !string.Equals(record.Activity.Code, TryForBabyCode, StringComparison.Ordinal))
                {
                    continue;
                }
                StartPregnancy(writer, pair.Key);
            }
            for (int i = 0; i < done.Count; i++)
            {
                pendingConceptions.Remove(done[i]);
            }
        }

        private void StartPregnancy(StateWriter writer, ResidentId carrier)
        {
            if (pregnancies.ContainsKey(carrier))
            {
                return;
            }
            ResidentRecord carrierRecord;
            if (!world.TryGetResident(carrier, out carrierRecord) || !carrierRecord.IsAlive)
            {
                return;
            }
            PartnerLink link;
            if (!partners.TryGetValue(carrier, out link) || !link.Consent)
            {
                return;
            }
            Capability carrierCapability;
            Capability otherCapability;
            if (!capabilities.TryGetValue(carrier, out carrierCapability) || !carrierCapability.CanCarry
                || !capabilities.TryGetValue(link.Partner, out otherCapability) || !otherCapability.CanImpregnate)
            {
                return;
            }
            double draw = new WorldRng(world.Seed).NextDouble();
            if (draw >= ConceptionChance)
            {
                return;
            }
            WorldEvent conception = world.AppendEvent(writer, "Conception", carrier,
                "other parent " + ((long)link.Partner).ToString());
            long due = Clockwork.GestationDueGameSeconds(world.GameSeconds);
            PregnancyState state = new PregnancyState();
            state.Record = new PregnancyRecord(carrier, link.Partner, conception.Event, due, 1);
            state.Household = carrierRecord.Household;
            pregnancies.Add(carrier, state);
            if (housing != null)
            {
                housing.SetNewbornReservations(writer, carrierRecord.Household, ReservationCount(carrierRecord.Household) + 1);
            }
        }

        private void ProcessPregnancies(StateWriter writer)
        {
            List<PregnancyState> all = new List<PregnancyState>(pregnancies.Values);
            for (int i = 0; i < all.Count; i++)
            {
                PregnancyState state = all[i];
                ResidentRecord carrier;
                if (!world.TryGetResident(state.Record.Carrier, out carrier) || !carrier.IsAlive)
                {
                    InterruptPregnancy(writer, state, "carrier died");
                    continue;
                }
                if (world.GameSeconds < state.Record.DueGameTime)
                {
                    continue;
                }
                TryDeliver(writer, state, carrier);
            }
        }

        private bool TryDeliver(StateWriter writer, PregnancyState state, ResidentRecord carrier)
        {
            PregnancyRecord pregnancy = state.Record;
            if (!HomeExists(carrier.Home) || !HouseholdExists(carrier.Household))
            {
                return false;
            }
            ResidentId guardian = carrier.Id;
            ResidentRecord other;
            bool hasOther = world.TryGetResident(pregnancy.OtherParent, out other);
            if (!IsLivingCaregiver(carrier))
            {
                if (!hasOther || !IsLivingCaregiver(other))
                {
                    return false;
                }
                guardian = other.Id;
            }
            AppearanceRecord carrierAppearance;
            AppearanceRecord otherAppearance;
            if (!appearances.TryGet(pregnancy.Carrier, out carrierAppearance)
                || !appearances.TryGet(pregnancy.OtherParent, out otherAppearance))
            {
                return false;
            }
            ResidentRecord child = world.AllocateResident(writer, null, LifeStage.Newborn,
                carrier.Household, carrier.Home);
            world.AddFamilyLink(writer, child.Id, pregnancy.Carrier, "Parent");
            world.AddFamilyLink(writer, child.Id, pregnancy.OtherParent, "Parent");
            long bodySeed = world.Seed + BaseBodyStreamStep * (long)child.Id;
            AppearanceRecord childAppearance = InheritAppearance(writer, child.Id,
                carrierAppearance, otherAppearance, bodySeed);
            WorldEvent birthEvent = world.AppendEvent(writer, "Birth", pregnancy.Carrier,
                "child " + ((long)child.Id).ToString());
            world.AssignCare(writer, new CareAssignment(child.Id, guardian,
                new CarePlacement(carrier.Household, 0), birthEvent.Event));
            if (housing != null)
            {
                int current = ReservationCount(state.Household);
                int next = current - pregnancy.CapacityHold;
                housing.SetNewbornReservations(writer, state.Household, next < 0 ? 0 : next);
            }
            births.Add(child.Id, new BirthRecord(child.Id, pregnancy.Carrier,
                pregnancy.OtherParent, pregnancy.ConceptionEvent, birthEvent.Event,
                world.Seed, bodySeed, childAppearance.BaseBody,
                carrierAppearance, otherAppearance, childAppearance));
            pregnancies.Remove(pregnancy.Carrier);
            return true;
        }

        private AppearanceRecord InheritAppearance(StateWriter writer, ResidentId child,
            AppearanceRecord carrier, AppearanceRecord other, long bodySeed)
        {
            appearances.Register(child, new WorldRng(bodySeed + 1L));
            appearances.SetPreset(writer, child, AppearanceDimension.SkinTone,
                (carrier.SkinTone + other.SkinTone) / 2);
            appearances.SetPreset(writer, child, AppearanceDimension.EyeColor,
                (carrier.EyeColor + other.EyeColor) / 2);
            appearances.SetPreset(writer, child, AppearanceDimension.HairColor,
                (carrier.HairColor + other.HairColor) / 2);
            appearances.SetPreset(writer, child, AppearanceDimension.Face, carrier.Face);
            appearances.SetPreset(writer, child, AppearanceDimension.HairType, carrier.HairType);
            appearances.SetPreset(writer, child, AppearanceDimension.HairStyle,
                ResidentFactory.ValidHairStyles(carrier.HairType)[0]);
            appearances.SetPreset(writer, child, AppearanceDimension.Accessory, carrier.Accessory);
            BaseBody body = (BaseBody)new WorldRng(bodySeed).NextInt(0, 2);
            appearances.SetBaseBody(writer, child, body);
            AppearanceRecord saved;
            appearances.TryGet(child, out saved);
            return saved;
        }

        private void InterruptPregnancy(StateWriter writer, PregnancyState state, string reason)
        {
            PregnancyRecord pregnancy = state.Record;
            world.AppendEvent(writer, "PregnancyInterrupted", pregnancy.Carrier, reason);
            if (housing != null)
            {
                int current = ReservationCount(state.Household);
                int next = current - pregnancy.CapacityHold;
                housing.SetNewbornReservations(writer, state.Household, next < 0 ? 0 : next);
            }
            pregnancies.Remove(pregnancy.Carrier);
        }

        private bool BedAccepted(ResidentRecord carrier, ResidentRecord other)
        {
            if (IsCloseFamily(carrier, other.Id) || IsCloseFamily(other, carrier.Id))
            {
                return false;
            }
            if (housing == null)
            {
                return true;
            }
            int beds = BedsFor(carrier.Home);
            int nonDependents = 0;
            int dependents = 0;
            int caregivers = 0;
            Housing.HousingSnapshot snapshot = housing.Export();
            ResidentRecord[] members = world.ResidentsSnapshot();
            for (int i = 0; i < members.Length; i++)
            {
                if (members[i].Household != carrier.Household)
                {
                    continue;
                }
                if (IsDependent(members[i].Stage))
                {
                    dependents++;
                }
                else
                {
                    nonDependents++;
                }
                if (IsCaregiver(snapshot, members[i].Id))
                {
                    caregivers++;
                }
            }
            if (caregivers == 0)
            {
                return false;
            }
            int need = nonDependents + Math.Max(0, dependents + 1 - caregivers * 2);
            return need <= beds;
        }

        private static bool IsCloseFamily(ResidentRecord resident, ResidentId other)
        {
            for (int i = 0; i < resident.Family.Count; i++)
            {
                FamilyLink link = resident.Family[i];
                if (link.Other == other
                    && (string.Equals(link.Kind, "Parent", StringComparison.Ordinal)
                        || string.Equals(link.Kind, "Child", StringComparison.Ordinal)
                        || string.Equals(link.Kind, "Sibling", StringComparison.Ordinal)))
                {
                    return true;
                }
            }
            return false;
        }

        private bool IsLivingCaregiver(ResidentRecord resident)
        {
            if (housing == null || !resident.IsAlive || resident.Stage < LifeStage.YoungAdult)
            {
                return false;
            }
            return IsCaregiver(housing.Export(), resident.Id);
        }

        private static bool IsCaregiver(Housing.HousingSnapshot snapshot, ResidentId id)
        {
            for (int i = 0; i < snapshot.Caregivers.Count; i++)
            {
                if (snapshot.Caregivers[i] == (long)id)
                {
                    return true;
                }
            }
            return false;
        }

        private int ReservationCount(HouseholdId household)
        {
            if (housing == null)
            {
                return 0;
            }
            Housing.HousingSnapshot snapshot = housing.Export();
            for (int i = 0; i < snapshot.Reservations.Count; i++)
            {
                if (snapshot.Reservations[i].Household == (long)household)
                {
                    return snapshot.Reservations[i].Count;
                }
            }
            return 0;
        }

        private int BedsFor(HomeId home)
        {
            List<HomeRecord> homes = world.ExportSnapshot().Homes;
            for (int i = 0; i < homes.Count; i++)
            {
                if (homes[i].Id == home)
                {
                    return homes[i].BedCount;
                }
            }
            return 0;
        }

        private bool HomeExists(HomeId home)
        {
            List<HomeRecord> homes = world.ExportSnapshot().Homes;
            for (int i = 0; i < homes.Count; i++)
            {
                if (homes[i].Id == home)
                {
                    return true;
                }
            }
            return false;
        }

        private bool HouseholdExists(HouseholdId household)
        {
            List<HouseholdRecord> households = world.ExportSnapshot().Households;
            for (int i = 0; i < households.Count; i++)
            {
                if (households[i].Id == household)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsDependent(LifeStage stage)
        {
            return stage == LifeStage.Newborn || stage == LifeStage.Infant || stage == LifeStage.Toddler;
        }

        private ActionEntry FindAction(ResidentId resident, string code)
        {
            ActionEntry current = arbitrator.GetCurrent(resident);
            if (current != null && string.Equals(current.Activity.Code, code, StringComparison.Ordinal))
            {
                return current;
            }
            ActionEntry[] pending = arbitrator.GetPending(resident);
            for (int i = 0; i < pending.Length; i++)
            {
                if (string.Equals(pending[i].Activity.Code, code, StringComparison.Ordinal))
                {
                    return pending[i];
                }
            }
            return null;
        }

        private ResidentRecord RequireResident(ResidentId id)
        {
            ResidentRecord record;
            if (!world.TryGetResident(id, out record))
            {
                throw new ArgumentException("Unknown resident " + ((long)id).ToString(), "id");
            }
            return record;
        }

        private WorldEvent RequireEvent(EventId id, string kind)
        {
            for (int i = 0; i < world.Events.Count; i++)
            {
                WorldEvent entry = world.Events[i];
                if (entry.Event == id && string.Equals(entry.Kind, kind, StringComparison.Ordinal))
                {
                    return entry;
                }
            }
            throw new ArgumentException("Missing " + kind + " event " + ((long)id).ToString(), "snapshot");
        }

        private static bool HasParentLink(ResidentRecord child, ResidentId parent)
        {
            for (int i = 0; i < child.Family.Count; i++)
            {
                if (child.Family[i].Other == parent
                    && string.Equals(child.Family[i].Kind, "Parent", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static void CheckWriter(StateWriter writer, string detail)
        {
            if (writer != StateWriter.SimCore && writer != StateWriter.CommandArbitrator && writer != StateWriter.ActivityExecutor)
            {
                throw new ArgumentException("Unknown state writer", "writer");
            }
        }

        private static AppearanceEntry ToEntry(AppearanceRecord record)
        {
            AppearanceEntry entry = new AppearanceEntry();
            entry.ResidentId = (long)record.Id;
            entry.SkinTone = record.SkinTone;
            entry.Face = record.Face;
            entry.EyeColor = record.EyeColor;
            entry.HairType = record.HairType;
            entry.HairStyle = record.HairStyle;
            entry.HairColor = record.HairColor;
            entry.Accessory = record.Accessory;
            entry.OutfitEveryday = record.GetOutfit(OutfitCategory.Everyday);
            entry.OutfitSleep = record.GetOutfit(OutfitCategory.Sleep);
            entry.OutfitExercise = record.GetOutfit(OutfitCategory.Exercise);
            entry.OverrideCategory = record.OverrideCategory;
            entry.BaseBody = record.BaseBody;
            return entry;
        }

        private static AppearanceRecord FromEntry(AppearanceEntry entry)
        {
            ResidentFactory.CheckPresetRange(AppearanceDimension.SkinTone, entry.SkinTone);
            ResidentFactory.CheckPresetRange(AppearanceDimension.Face, entry.Face);
            ResidentFactory.CheckPresetRange(AppearanceDimension.EyeColor, entry.EyeColor);
            ResidentFactory.CheckPresetRange(AppearanceDimension.HairType, entry.HairType);
            ResidentFactory.CheckPresetRange(AppearanceDimension.HairStyle, entry.HairStyle);
            ResidentFactory.CheckPresetRange(AppearanceDimension.HairColor, entry.HairColor);
            ResidentFactory.CheckPresetRange(AppearanceDimension.Accessory, entry.Accessory);
            if (!ResidentFactory.IsValidHairStyle(entry.HairType, entry.HairStyle))
            {
                throw new ArgumentException("Hair style " + entry.HairStyle.ToString()
                    + " is not valid for hair type " + entry.HairType.ToString(), "entry");
            }
            ResidentFactory.CheckPresetRange(AppearanceDimension.Clothing, entry.OutfitEveryday);
            ResidentFactory.CheckPresetRange(AppearanceDimension.Clothing, entry.OutfitSleep);
            ResidentFactory.CheckPresetRange(AppearanceDimension.Clothing, entry.OutfitExercise);
            if (entry.OverrideCategory < -1 || entry.OverrideCategory >= ResidentFactory.OutfitCategoryCount)
            {
                throw new ArgumentException("Override category out of range", "entry");
            }
            ResidentFactory.CheckBaseBodyRange((int)entry.BaseBody);
            AppearanceRecord record = new AppearanceRecord((ResidentId)entry.ResidentId);
            record.SetPreset(0, entry.SkinTone);
            record.SetPreset(1, entry.Face);
            record.SetPreset(2, entry.EyeColor);
            record.SetPreset(3, entry.HairType);
            record.SetPreset(4, entry.HairStyle);
            record.SetPreset(5, entry.HairColor);
            record.SetPreset(7, entry.Accessory);
            record.SetOutfit(OutfitCategory.Everyday, entry.OutfitEveryday);
            record.SetOutfit(OutfitCategory.Sleep, entry.OutfitSleep);
            record.SetOutfit(OutfitCategory.Exercise, entry.OutfitExercise);
            record.SetOverride(entry.OverrideCategory);
            record.SetBaseBody(entry.BaseBody);
            return record;
        }
    }
}
