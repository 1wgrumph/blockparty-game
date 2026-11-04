// CREATIVE@1: creative activities and per-viewer display awards (BP-ACT-12).
// Consumes STATE@1 (WorldState residents, skills, traits, memories, events,
// effect ledger) and the executor's committed-ledger boundary. Plain C# 9, no
// engine dependency. Completion is a post-executor boundary like
// RELATION.Complete: it accepts one committed MakeWallArt or PracticeCreative
// action per ActionInstanceId and never applies twice. MakeWallArt charges 10
// materials, grants Creativity +15 and records a WorkItem whose author,
// personality summary, chosen factual memory and skill-band quality are
// snapshots. PracticeCreative grants Creativity +15 and Fun +10 without an
// item. DisplayWork places an existing item; a viewer within 5 m during Chat
// gains Fun +5 at most once per viewer/item/game day.
// No static mutable state; all writes flow through audited WorldState methods
// with the caller StateWriter.
using System;
using System.Collections.Generic;
using System.Globalization;
using Blockparty.Contracts;
using Blockparty.Simulation;

namespace Blockparty.Activities
{
    public sealed class WorkItem
    {
        public WorkItemRef Reference { get; private set; }
        public string QualityTier { get; private set; }

        internal WorkItem(WorkItemRef reference, string qualityTier)
        {
            Reference = reference;
            QualityTier = qualityTier ?? string.Empty;
        }
    }

    // One successful display view: the award ledger key is (viewer, item, day).
    public readonly struct ViewerAward : IEquatable<ViewerAward>
    {
        public readonly ResidentId Viewer;
        public readonly ObjectId Item;
        public readonly long GameDay;

        public ViewerAward(ResidentId viewer, ObjectId item, long gameDay)
        {
            Viewer = viewer;
            Item = item;
            GameDay = gameDay;
        }

        public bool Equals(ViewerAward other)
        {
            return Viewer == other.Viewer && Item == other.Item && GameDay == other.GameDay;
        }

        public override bool Equals(object obj)
        {
            return obj is ViewerAward other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                long hash = (long)Viewer;
                hash = (hash * 397L) ^ (long)Item;
                hash = (hash * 397L) ^ GameDay;
                return hash.GetHashCode();
            }
        }
    }

    // Save capture bundle: work items with their recorded inputs and quality,
    // the per-viewer daily award ledger, applied completion ids and placed
    // items. Rows carry raw ids; no action remap applies because completion
    // ids are terminal and never reallocated.
    public sealed class CreativeSnapshot
    {
        public sealed class WorkRow
        {
            public long Item;
            public long Author;
            public string PersonalitySummary;
            public long MemoryEvent;
            public string QualityTier;
        }

        public sealed class AwardRow
        {
            public long Viewer;
            public long Item;
            public long GameDay;
        }

        public sealed class AppliedRow
        {
            public long Action;
        }

        public sealed class DisplayRow
        {
            public long Item;
            public long Wall;
        }

        public readonly List<WorkRow> Works = new List<WorkRow>();
        public readonly List<AwardRow> Awards = new List<AwardRow>();
        public readonly List<AppliedRow> Applied = new List<AppliedRow>();
        public readonly List<DisplayRow> Displays = new List<DisplayRow>();
    }

    public sealed class Creative
    {
        public const long DayGameSeconds = 86400L;

        private const string MakeWallArtCode = "ACT-CREA-01";
        private const string PracticeCode = "ACT-CREA-02";
        private const string CreativitySkill = "Creativity";
        private const long MaterialCost = 10L;
        private const int SkillGain = 15;
        private const float PracticeFunGain = 10.0f;
        private const float ViewFunGain = 5.0f;
        private const float ViewRangeMetres = 5.0f;
        private const string CrudeTier = "crude";
        private const string PlainTier = "plain";
        private const string FineTier = "fine";

        private readonly WorldState world;
        private readonly List<WorkItem> works = new List<WorkItem>();
        private readonly Dictionary<ObjectId, WorkItem> worksById =
            new Dictionary<ObjectId, WorkItem>();
        private readonly List<ViewerAward> awards = new List<ViewerAward>();
        private readonly Dictionary<ViewerAward, bool> awarded =
            new Dictionary<ViewerAward, bool>();
        private readonly HashSet<ActionInstanceId> applied =
            new HashSet<ActionInstanceId>();
        private readonly Dictionary<ObjectId, ObjectId> displays =
            new Dictionary<ObjectId, ObjectId>();

        public Creative(WorldState world)
        {
            if (world == null)
            {
                throw new ArgumentNullException("world");
            }
            this.world = world;
        }

        public IReadOnlyList<WorkItem> WorkItems
        {
            get { return works.AsReadOnly(); }
        }

        public IReadOnlyList<ViewerAward> ViewerAwards
        {
            get { return awards.AsReadOnly(); }
        }

        public bool Complete(StateWriter writer, ActionInstanceId action, ActivityId activity,
            ResidentId author, EventId chosenMemory)
        {
            CheckWriter(writer);
            if (applied.Contains(action))
            {
                return false;
            }
            string code = activity.Code ?? string.Empty;
            bool make = string.Equals(code, MakeWallArtCode, StringComparison.Ordinal);
            bool practice = string.Equals(code, PracticeCode, StringComparison.Ordinal);
            if (!make && !practice)
            {
                return false;
            }
            if (!CommittedFor(action, code, author))
            {
                return false;
            }
            ResidentRecord record;
            if (!world.TryGetResident(author, out record) || !record.IsAlive)
            {
                return false;
            }
            if (make)
            {
                if (record.Money < MaterialCost)
                {
                    return false;
                }
                if (!KnownFactualMemory(record, chosenMemory))
                {
                    return false;
                }
                string tier = QualityTier(SkillLevel(record));
                string summary = PersonalitySummary(record);
                ObjectId item = world.AllocateObject(writer);
                world.SetMoney(writer, author, record.Money - MaterialCost);
                world.AddSkillPoints(writer, author, CreativitySkill, SkillGain);
                WorkItem created = new WorkItem(
                    new WorkItemRef(item, author, summary, chosenMemory), tier);
                works.Add(created);
                worksById.Add(item, created);
                applied.Add(action);
                return true;
            }
            float fun = record.GetNeed(Need.Fun) + PracticeFunGain;
            world.AddSkillPoints(writer, author, CreativitySkill, SkillGain);
            world.SetNeed(writer, author, Need.Fun, fun);
            applied.Add(action);
            return true;
        }

        public bool Display(StateWriter writer, ObjectId item, ObjectId wall)
        {
            CheckWriter(writer);
            if (!worksById.ContainsKey(item) || (long)wall <= 0L)
            {
                return false;
            }
            displays[item] = wall;
            return true;
        }

        public bool ViewDuringChat(StateWriter writer, ResidentId viewer, ObjectId item,
            float distanceMetres)
        {
            CheckWriter(writer);
            if (!(distanceMetres >= 0.0f && distanceMetres <= ViewRangeMetres))
            {
                return false;
            }
            if (!worksById.ContainsKey(item) || !displays.ContainsKey(item))
            {
                return false;
            }
            ResidentRecord record;
            if (!world.TryGetResident(viewer, out record))
            {
                return false;
            }
            ViewerAward award = new ViewerAward(viewer, item, world.GameSeconds / DayGameSeconds);
            if (awarded.ContainsKey(award))
            {
                return false;
            }
            world.SetNeed(writer, viewer, Need.Fun, record.GetNeed(Need.Fun) + ViewFunGain);
            awards.Add(award);
            awarded.Add(award, true);
            return true;
        }

        public CreativeSnapshot Export()
        {
            CreativeSnapshot snapshot = new CreativeSnapshot();
            List<WorkItem> sortedWorks = new List<WorkItem>(works);
            for (int i = 1; i < sortedWorks.Count; i++)
            {
                WorkItem row = sortedWorks[i];
                int j = i - 1;
                while (j >= 0 && (long)sortedWorks[j].Reference.Item > (long)row.Reference.Item)
                {
                    sortedWorks[j + 1] = sortedWorks[j];
                    j--;
                }
                sortedWorks[j + 1] = row;
            }
            for (int i = 0; i < sortedWorks.Count; i++)
            {
                WorkItem item = sortedWorks[i];
                CreativeSnapshot.WorkRow row = new CreativeSnapshot.WorkRow();
                row.Item = (long)item.Reference.Item;
                row.Author = (long)item.Reference.Author;
                row.PersonalitySummary = item.Reference.PersonalitySummary;
                row.MemoryEvent = (long)item.Reference.MemoryEvent;
                row.QualityTier = item.QualityTier;
                snapshot.Works.Add(row);
            }
            List<ViewerAward> sortedAwards = new List<ViewerAward>(awards);
            for (int i = 1; i < sortedAwards.Count; i++)
            {
                ViewerAward row = sortedAwards[i];
                int j = i - 1;
                while (j >= 0 && CompareAwards(sortedAwards[j], row) > 0)
                {
                    sortedAwards[j + 1] = sortedAwards[j];
                    j--;
                }
                sortedAwards[j + 1] = row;
            }
            for (int i = 0; i < sortedAwards.Count; i++)
            {
                CreativeSnapshot.AwardRow row = new CreativeSnapshot.AwardRow();
                row.Viewer = (long)sortedAwards[i].Viewer;
                row.Item = (long)sortedAwards[i].Item;
                row.GameDay = sortedAwards[i].GameDay;
                snapshot.Awards.Add(row);
            }
            List<ActionInstanceId> sortedApplied = new List<ActionInstanceId>(applied);
            for (int i = 1; i < sortedApplied.Count; i++)
            {
                ActionInstanceId row = sortedApplied[i];
                int j = i - 1;
                while (j >= 0 && (long)sortedApplied[j] > (long)row)
                {
                    sortedApplied[j + 1] = sortedApplied[j];
                    j--;
                }
                sortedApplied[j + 1] = row;
            }
            for (int i = 0; i < sortedApplied.Count; i++)
            {
                CreativeSnapshot.AppliedRow row = new CreativeSnapshot.AppliedRow();
                row.Action = (long)sortedApplied[i];
                snapshot.Applied.Add(row);
            }
            List<ObjectId> sortedDisplays = new List<ObjectId>(displays.Keys);
            for (int i = 1; i < sortedDisplays.Count; i++)
            {
                ObjectId row = sortedDisplays[i];
                int j = i - 1;
                while (j >= 0 && (long)sortedDisplays[j] > (long)row)
                {
                    sortedDisplays[j + 1] = sortedDisplays[j];
                    j--;
                }
                sortedDisplays[j + 1] = row;
            }
            for (int i = 0; i < sortedDisplays.Count; i++)
            {
                CreativeSnapshot.DisplayRow row = new CreativeSnapshot.DisplayRow();
                row.Item = (long)sortedDisplays[i];
                row.Wall = (long)displays[sortedDisplays[i]];
                snapshot.Displays.Add(row);
            }
            return snapshot;
        }

        public void Import(StateWriter writer, CreativeSnapshot snapshot)
        {
            CheckWriter(writer);
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }
            if (snapshot.Works == null || snapshot.Awards == null
                || snapshot.Applied == null || snapshot.Displays == null)
            {
                throw new ArgumentException("Creative bundle lists must exist", "snapshot");
            }
            List<WorkItem> nextWorks = new List<WorkItem>();
            Dictionary<ObjectId, WorkItem> nextById = new Dictionary<ObjectId, WorkItem>();
            for (int i = 0; i < snapshot.Works.Count; i++)
            {
                CreativeSnapshot.WorkRow row = snapshot.Works[i];
                if (row == null || row.PersonalitySummary == null)
                {
                    throw new ArgumentException("Null creative work row " + i.ToString(), "snapshot");
                }
                ObjectId item = (ObjectId)row.Item;
                if ((long)item <= 0L || (long)item >= world.NextObjectId)
                {
                    throw new ArgumentException("Creative work item " + row.Item.ToString()
                        + " is beyond the saved id high-water mark", "snapshot");
                }
                if (nextById.ContainsKey(item))
                {
                    throw new ArgumentException("Duplicate creative work item "
                        + row.Item.ToString(), "snapshot");
                }
                ResidentRecord author;
                if (!world.TryGetResident((ResidentId)row.Author, out author))
                {
                    throw new ArgumentException("Creative work author " + row.Author.ToString()
                        + " is not a resident", "snapshot");
                }
                if (!IsQualityTier(row.QualityTier))
                {
                    throw new ArgumentException("Creative work item " + row.Item.ToString()
                        + " has an unknown quality tier", "snapshot");
                }
                EventId memory = (EventId)row.MemoryEvent;
                if (!KnownFactualMemory(author, memory))
                {
                    throw new ArgumentException("Creative work item " + row.Item.ToString()
                        + " carries a memory the author does not hold", "snapshot");
                }
                WorkItem restored = new WorkItem(
                    new WorkItemRef(item, author.Id, row.PersonalitySummary, memory),
                    row.QualityTier);
                nextWorks.Add(restored);
                nextById.Add(item, restored);
            }
            List<ViewerAward> nextAwards = new List<ViewerAward>();
            Dictionary<ViewerAward, bool> nextAwarded = new Dictionary<ViewerAward, bool>();
            long currentDay = world.GameSeconds / DayGameSeconds;
            for (int i = 0; i < snapshot.Awards.Count; i++)
            {
                CreativeSnapshot.AwardRow row = snapshot.Awards[i];
                if (row == null || row.GameDay < 0L || row.GameDay > currentDay)
                {
                    throw new ArgumentException("Invalid creative award row " + i.ToString(), "snapshot");
                }
                ResidentRecord viewer;
                if (!world.TryGetResident((ResidentId)row.Viewer, out viewer))
                {
                    throw new ArgumentException("Creative award viewer " + row.Viewer.ToString()
                        + " is not a resident", "snapshot");
                }
                ObjectId item = (ObjectId)row.Item;
                if (!nextById.ContainsKey(item))
                {
                    throw new ArgumentException("Creative award references unknown item "
                        + row.Item.ToString(), "snapshot");
                }
                ViewerAward award = new ViewerAward(viewer.Id, item, row.GameDay);
                if (nextAwarded.ContainsKey(award))
                {
                    throw new ArgumentException("Duplicate creative award for item "
                        + row.Item.ToString(), "snapshot");
                }
                nextAwards.Add(award);
                nextAwarded.Add(award, true);
            }
            HashSet<ActionInstanceId> nextApplied = new HashSet<ActionInstanceId>();
            for (int i = 0; i < snapshot.Applied.Count; i++)
            {
                CreativeSnapshot.AppliedRow row = snapshot.Applied[i];
                if (row == null || row.Action <= 0L || row.Action >= world.NextActionId)
                {
                    throw new ArgumentException("Invalid creative applied action "
                        + (row == null ? i.ToString() : row.Action.ToString()), "snapshot");
                }
                ActionInstanceId action = (ActionInstanceId)row.Action;
                if (!nextApplied.Add(action))
                {
                    throw new ArgumentException("Duplicate creative applied action "
                        + row.Action.ToString(), "snapshot");
                }
            }
            Dictionary<ObjectId, ObjectId> nextDisplays = new Dictionary<ObjectId, ObjectId>();
            for (int i = 0; i < snapshot.Displays.Count; i++)
            {
                CreativeSnapshot.DisplayRow row = snapshot.Displays[i];
                if (row == null)
                {
                    throw new ArgumentException("Null creative display row " + i.ToString(), "snapshot");
                }
                ObjectId item = (ObjectId)row.Item;
                if (!nextById.ContainsKey(item))
                {
                    throw new ArgumentException("Creative display references unknown item "
                        + row.Item.ToString(), "snapshot");
                }
                if (row.Wall <= 0L || row.Wall >= world.NextObjectId)
                {
                    throw new ArgumentException("Creative display wall " + row.Wall.ToString()
                        + " is beyond the saved id high-water mark", "snapshot");
                }
                if (nextDisplays.ContainsKey(item))
                {
                    throw new ArgumentException("Duplicate creative display for item "
                        + row.Item.ToString(), "snapshot");
                }
                nextDisplays.Add(item, (ObjectId)row.Wall);
            }
            works.Clear();
            works.AddRange(nextWorks);
            worksById.Clear();
            foreach (KeyValuePair<ObjectId, WorkItem> pair in nextById)
            {
                worksById[pair.Key] = pair.Value;
            }
            awards.Clear();
            awards.AddRange(nextAwards);
            awarded.Clear();
            foreach (KeyValuePair<ViewerAward, bool> pair in nextAwarded)
            {
                awarded[pair.Key] = pair.Value;
            }
            applied.Clear();
            foreach (ActionInstanceId action in nextApplied)
            {
                applied.Add(action);
            }
            displays.Clear();
            foreach (KeyValuePair<ObjectId, ObjectId> pair in nextDisplays)
            {
                displays[pair.Key] = pair.Value;
            }
        }

        internal bool HasSaveState()
        {
            return works.Count != 0 || awards.Count != 0
                || applied.Count != 0 || displays.Count != 0;
        }

        // Completion is refused unless the executor committed this exact
        // action, for this author, as this activity.
        private bool CommittedFor(ActionInstanceId action, string code, ResidentId author)
        {
            IReadOnlyList<EffectLedgerEntry> ledger = world.EffectLedger;
            for (int i = 0; i < ledger.Count; i++)
            {
                EffectLedgerEntry entry = ledger[i];
                if (entry.Action != action)
                {
                    continue;
                }
                return entry.State == LedgerState.Committed
                    && entry.Recipient.First == author
                    && string.Equals(entry.EffectKind, code, StringComparison.Ordinal);
            }
            return false;
        }

        private bool KnownFactualMemory(ResidentRecord author, EventId memory)
        {
            if ((long)memory <= 0L)
            {
                return false;
            }
            bool known = false;
            IReadOnlyList<MemoryEntry> memories = author.Memories;
            for (int i = 0; i < memories.Count; i++)
            {
                if (memories[i].Event == memory)
                {
                    known = true;
                    break;
                }
            }
            if (!known)
            {
                return false;
            }
            IReadOnlyList<WorldEvent> events = world.Events;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].Event == memory)
                {
                    return true;
                }
            }
            return false;
        }

        private static int SkillLevel(ResidentRecord record)
        {
            IReadOnlyList<SkillEntry> skills = record.Skills;
            for (int i = 0; i < skills.Count; i++)
            {
                if (string.Equals(skills[i].SkillId, CreativitySkill, StringComparison.Ordinal))
                {
                    return skills[i].Level;
                }
            }
            return 0;
        }

        // Quality records the skill the author brought to the work, before the
        // completion's own +15 progress.
        private static string QualityTier(int level)
        {
            if (level <= 2)
            {
                return CrudeTier;
            }
            if (level <= 6)
            {
                return PlainTier;
            }
            return FineTier;
        }

        private static bool IsQualityTier(string tier)
        {
            return string.Equals(tier, CrudeTier, StringComparison.Ordinal)
                || string.Equals(tier, PlainTier, StringComparison.Ordinal)
                || string.Equals(tier, FineTier, StringComparison.Ordinal);
        }

        private static string PersonalitySummary(ResidentRecord record)
        {
            return "Sociable " + record.GetTrait(TraitAxis.Sociable).ToString(CultureInfo.InvariantCulture)
                + ", Ambitious " + record.GetTrait(TraitAxis.Ambitious).ToString(CultureInfo.InvariantCulture)
                + ", Active " + record.GetTrait(TraitAxis.Active).ToString(CultureInfo.InvariantCulture)
                + ", Creative " + record.GetTrait(TraitAxis.Creative).ToString(CultureInfo.InvariantCulture)
                + ", Neat " + record.GetTrait(TraitAxis.Neat).ToString(CultureInfo.InvariantCulture)
                + ", Playful " + record.GetTrait(TraitAxis.Playful).ToString(CultureInfo.InvariantCulture);
        }

        private static int CompareAwards(ViewerAward a, ViewerAward b)
        {
            if ((long)a.Viewer != (long)b.Viewer)
            {
                return (long)a.Viewer < (long)b.Viewer ? -1 : 1;
            }
            if ((long)a.Item != (long)b.Item)
            {
                return (long)a.Item < (long)b.Item ? -1 : 1;
            }
            if (a.GameDay != b.GameDay)
            {
                return a.GameDay < b.GameDay ? -1 : 1;
            }
            return 0;
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
