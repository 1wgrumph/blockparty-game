// SELECT-ACTION@1: resident selection and outline projection (BP-UI-02).
// Selection identity and clearing live in WorldNavigation, so click, focus
// and keyboard traversal share one persistent ResidentId and never write
// authoritative state. Snap uses caller-projected 1080p centres; equal
// distances resolve to the ascending ResidentId. Plain C# 9, no UnityEngine
// dependency. Fast Enter Play Mode: all state on the instance.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;
using Blockparty.Simulation;

namespace Blockparty.UI
{
    // One visible, selectable resident projection supplied by the view.
    public sealed class SelectionTarget
    {
        public readonly ResidentId Id;
        public readonly float X, Y;

        public SelectionTarget(ResidentId id, float x, float y)
        {
            Id = id;
            X = x;
            Y = y;
        }
    }

    public sealed class Selection
    {
        // WorldNavigation has no clear-selection method; a catalog-miss search
        // is its published clear path. shortcut: revisit when UI-NAV adds one.
        private const string NoActivityCode = "__select-action-none__";

        private readonly WorldState world;
        private readonly WorldNavigation navigation;
        private readonly Accessibility accessibility;
        private string message = string.Empty;

        public Selection(WorldState world, WorldNavigation navigation,
            Accessibility accessibility)
        {
            if (world == null)
            {
                throw new ArgumentNullException("world");
            }
            if (navigation == null)
            {
                throw new ArgumentNullException("navigation");
            }
            if (accessibility == null)
            {
                throw new ArgumentNullException("accessibility");
            }
            this.world = world;
            this.navigation = navigation;
            this.accessibility = accessibility;
        }

        public string Label
        {
            get
            {
                ResidentId? selected = navigation.SelectedResident;
                ResidentRecord record;
                if (selected.HasValue && world.TryGetResident(selected.Value, out record))
                {
                    return record.DisplayName;
                }
                return string.Empty;
            }
        }

        public string Message { get { return message; } }

        public bool HasOutline(ResidentId resident)
        {
            return navigation.SelectedResident.HasValue
                && navigation.SelectedResident.Value == resident;
        }

        // Snap activation uses projected centres; without snap the hit wins.
        public void PointerSelect(ResidentId? hit, float x, float y,
            IReadOnlyList<SelectionTarget> targets)
        {
            ResidentId? chosen = accessibility.Settings.SnapSelection
                ? Nearest(targets, x, y)
                : hit;
            if (!chosen.HasValue)
            {
                Clear("No resident candidate.");
                return;
            }
            Select(chosen.Value);
        }

        public bool FocusResident(ResidentId resident)
        {
            return Select(resident);
        }

        // Keyboard traversal follows the caller's declared reading order; a
        // model-only caller without projections falls back to identity order.
        public bool TryInput(string binding, IReadOnlyList<SelectionTarget> targets)
        {
            string action = accessibility.ResolveInput(binding);
            if (action != "SelectNext" && action != "SelectPrevious")
            {
                return false;
            }
            List<ResidentId> ids = TargetIds(targets);
            if (ids.Count == 0)
            {
                ids = ResidentIds();
            }
            if (ids.Count == 0)
            {
                Clear("No resident candidate.");
                return false;
            }
            int index = -1;
            if (navigation.SelectedResident.HasValue)
            {
                index = ids.IndexOf(navigation.SelectedResident.Value);
            }
            int step = action == "SelectNext" ? 1 : -1;
            index = index < 0
                ? (step > 0 ? 0 : ids.Count - 1)
                : (index + step + ids.Count) % ids.Count;
            return Select(ids[index]);
        }

        private bool Select(ResidentId resident)
        {
            CommandResult result = navigation.SelectResident(resident);
            message = result.Accepted ? string.Empty : navigation.Message;
            return result.Accepted;
        }

        private void Clear(string reason)
        {
            navigation.SearchByActivity(NoActivityCode);
            message = reason;
        }

        private static List<ResidentId> TargetIds(IReadOnlyList<SelectionTarget> targets)
        {
            List<ResidentId> ids = new List<ResidentId>();
            if (targets == null)
            {
                return ids;
            }
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i] != null)
                {
                    ids.Add(targets[i].Id);
                }
            }
            return ids;
        }

        private List<ResidentId> ResidentIds()
        {
            ResidentRecord[] residents = world.ResidentsSnapshot();
            List<ResidentId> ids = new List<ResidentId>(residents.Length);
            for (int i = 0; i < residents.Length; i++)
            {
                ids.Add(residents[i].Id);
            }
            ids.Sort((a, b) => ((long)a).CompareTo((long)b));
            return ids;
        }

        private static ResidentId? Nearest(IReadOnlyList<SelectionTarget> targets,
            float x, float y)
        {
            if (targets == null)
            {
                return null;
            }
            bool found = false;
            ResidentId best = default(ResidentId);
            double bestDistance = 0.0;
            for (int i = 0; i < targets.Count; i++)
            {
                SelectionTarget candidate = targets[i];
                if (candidate == null)
                {
                    continue;
                }
                double dx = candidate.X - x;
                double dy = candidate.Y - y;
                double distance = dx * dx + dy * dy;
                if (!found || distance < bestDistance
                    || (distance == bestDistance && (long)candidate.Id < (long)best))
                {
                    found = true;
                    best = candidate.Id;
                    bestDistance = distance;
                }
            }
            return found ? (ResidentId?)best : null;
        }
    }
}
