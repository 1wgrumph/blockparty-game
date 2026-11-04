// BLOCK-SITES@1: in-block destination registry (BP-WORLD-01/BP-UI-03).
// The six block modules are registered destinations with stable ids, entry
// poses and interaction slots (residences advertise no interaction slot;
// their activities belong to HOME). Layout metadata lives in code
// (Allocation D-2); the metre-space anchor offsets below are copied
// from the landed ENV assets' blockparty extras, translated by each site's
// origin. Each site and slot allocates a WorldState object id and registers
// one approach pose with NAV, so WORK/EDUCATION/follow travel to real
// in-block targets. Plain C# 9, no UnityEngine dependency.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;

namespace Blockparty.Simulation
{
    public sealed class BlockSiteSlot
    {
        public BlockSiteSlot(string id, string activityCode, string objectKind,
            ObjectId obj, ApproachPose pose)
        {
            Id = id;
            ActivityCode = activityCode;
            ObjectKind = objectKind;
            Object = obj;
            Pose = pose;
        }

        public string Id { get; private set; }
        public string ActivityCode { get; private set; }
        public string ObjectKind { get; private set; }
        public ObjectId Object { get; private set; }
        public ApproachPose Pose { get; private set; }
    }

    public sealed class BlockSite
    {
        public BlockSite(string id, string assetPath, ObjectId obj,
            ApproachPose origin, ApproachPose entry, BlockSiteSlot[] slots)
        {
            Id = id;
            AssetPath = assetPath;
            Object = obj;
            Origin = origin;
            Entry = entry;
            Slots = slots == null
                ? new List<BlockSiteSlot>().AsReadOnly()
                : new List<BlockSiteSlot>(slots).AsReadOnly();
        }

        public string Id { get; private set; }
        public string AssetPath { get; private set; }
        public ObjectId Object { get; private set; }
        public ApproachPose Origin { get; private set; }
        public ApproachPose Entry { get; private set; }
        public IReadOnlyList<BlockSiteSlot> Slots { get; private set; }
    }

    public sealed class BlockSites
    {
        private sealed class SlotSpec
        {
            public string Id;
            public string Activity;
            public string Kind;
            public bool HasOffset;
            public float Dx;
            public float Dy;
            public float Dz;
        }

        private readonly List<BlockSite> sites = new List<BlockSite>();

        public BlockSites(WorldState world, ActivityExecutor executor)
        {
            if (world == null)
            {
                throw new ArgumentNullException("world");
            }
            if (executor == null)
            {
                throw new ArgumentNullException("executor");
            }
            Add(world, executor, "apartment",
                "Assets/Blockparty/Art/Environment/apartment-first.gltf",
                new ApproachPose(-30f, 0f, 0f), 0f, 0f, -2.5f);
            Add(world, executor, "common-room",
                "Assets/Blockparty/Art/Environment/second.gltf",
                new ApproachPose(-30f, 0f, 30f), 0f, 0f, -2.5f);
            Add(world, executor, "cafe",
                "Assets/Blockparty/Art/Environment/Workplaces/cafe.gltf",
                new ApproachPose(0f, 0f, 0f), 0f, 0f, -2.5f,
                new SlotSpec
                {
                    Id = "cafe-service", Activity = "ACT-WORK-01",
                    Kind = "Counter", HasOffset = false,
                });
            Add(world, executor, "workshop",
                "Assets/Blockparty/Art/Environment/Workplaces/workshop.gltf",
                new ApproachPose(30f, 0f, 0f), 0f, 0f, -2.5f,
                new SlotSpec
                {
                    Id = "workshop-bench", Activity = "ACT-WORK-02",
                    Kind = "Workbench", HasOffset = false,
                });
            Add(world, executor, "office",
                "Assets/Blockparty/Art/Environment/Civic/office.gltf",
                new ApproachPose(0f, 0f, 30f), 0f, 0f, -3.0f,
                new SlotSpec
                {
                    Id = "work-technician", Activity = "ACT-WORK-02",
                    Kind = "Computer", HasOffset = true,
                    Dx = -2.8f, Dy = 0f, Dz = 1.7999999523162842f,
                },
                new SlotSpec
                {
                    Id = "work-jobsearch", Activity = "ACT-WORK-03",
                    Kind = "Computer", HasOffset = true,
                    Dx = -2.8f, Dy = 0f, Dz = -0.6000000238418579f,
                },
                new SlotSpec
                {
                    Id = "work-ai-engineer", Activity = "ACT-WORK-04",
                    Kind = "Computer", HasOffset = true,
                    Dx = 2.8f, Dy = 0f, Dz = 1.7999999523162842f,
                });
            Add(world, executor, "school",
                "Assets/Blockparty/Art/Environment/Civic/school.gltf",
                new ApproachPose(30f, 0f, 30f), 0f, 0f, -3.0f,
                new SlotSpec
                {
                    Id = "attend-school-a", Activity = "ACT-EDU-01",
                    Kind = "Desk", HasOffset = true,
                    Dx = -2.05f, Dy = 0f, Dz = 1.149999976158142f,
                },
                new SlotSpec
                {
                    Id = "attend-school-b", Activity = "ACT-EDU-01",
                    Kind = "Desk", HasOffset = true,
                    Dx = 1.2500000476837159f, Dy = 0f, Dz = 1.149999976158142f,
                });
        }

        public IReadOnlyList<BlockSite> Sites { get { return sites.AsReadOnly(); } }

        private void Add(WorldState world, ActivityExecutor executor, string id,
            string assetPath, ApproachPose origin,
            float entryDx, float entryDy, float entryDz, params SlotSpec[] specs)
        {
            ApproachPose entry = new ApproachPose(
                origin.X + entryDx, origin.Y + entryDy, origin.Z + entryDz);
            ObjectId siteObject = world.AllocateObject(StateWriter.SimCore);
            executor.RegisterObject(siteObject, new ApproachPose[] { entry });
            List<BlockSiteSlot> slots = new List<BlockSiteSlot>(specs.Length);
            for (int i = 0; i < specs.Length; i++)
            {
                SlotSpec spec = specs[i];
                ApproachPose pose = spec.HasOffset
                    ? new ApproachPose(origin.X + spec.Dx, origin.Y + spec.Dy, origin.Z + spec.Dz)
                    : origin;
                ObjectId slotObject = world.AllocateObject(StateWriter.SimCore);
                executor.RegisterObject(slotObject, new ApproachPose[] { pose });
                slots.Add(new BlockSiteSlot(spec.Id, spec.Activity, spec.Kind, slotObject, pose));
            }
            sites.Add(new BlockSite(id, assetPath, siteObject, origin, entry, slots.ToArray()));
        }
    }
}
