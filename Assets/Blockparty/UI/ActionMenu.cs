// SELECT-ACTION@1: object action menu over normal player commands (BP-UI-02).
// The menu stores only the caller's object-bound offers, so it never
// enumerates global catalog activities. Choosing an offered activity submits
// one PlayerHuman DirectActivity through CommandArbitrator with the live
// resident sequence; the menu reports the accepted result or refusal reason.
// Immediate and Append are separate accessible choices. Plain C# 9, no
// UnityEngine dependency. Fast Enter Play Mode: all state on the instance.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;
using Blockparty.Simulation;

namespace Blockparty.UI
{
    public sealed class ActionMenu
    {
        // Presentation command ids stay above world/fixture ranges, mirroring
        // ResidentPanel; Submit validates state, not this id.
        private const long FirstCommandId = 2100000000L;

        private readonly WorldState world;
        private readonly CommandArbitrator commands;
        private readonly Accessibility accessibility;
        private readonly InputMap input;
        private readonly List<AvailableAction> activities = new List<AvailableAction>();
        private ResidentId resident;
        private ObjectId target;
        private string label = string.Empty;
        private string message = string.Empty;
        private string focusedId = string.Empty;
        private long nextCommandId = FirstCommandId;

        public ActionMenu(WorldState world, CommandArbitrator commands,
            Accessibility accessibility, InputMap input)
        {
            if (world == null)
            {
                throw new ArgumentNullException("world");
            }
            if (commands == null)
            {
                throw new ArgumentNullException("commands");
            }
            if (accessibility == null)
            {
                throw new ArgumentNullException("accessibility");
            }
            if (input == null)
            {
                throw new ArgumentNullException("input");
            }
            this.world = world;
            this.commands = commands;
            this.accessibility = accessibility;
            this.input = input;
        }

        public IReadOnlyList<AvailableAction> Activities
        {
            get { return activities.AsReadOnly(); }
        }

        public string Label { get { return label; } }
        public string Message { get { return message; } }
        public string FocusedElementId { get { return focusedId; } }

        // The caller supplies the selected resident, the object and exactly
        // that object's catalog offers; opening submits no command.
        public void Open(ResidentId resident, ObjectId target, string label,
            IReadOnlyList<AvailableAction> activities)
        {
            if (activities == null)
            {
                throw new ArgumentNullException("activities");
            }
            this.resident = resident;
            this.target = target;
            this.label = label ?? string.Empty;
            this.activities.Clear();
            for (int i = 0; i < activities.Count; i++)
            {
                this.activities.Add(activities[i]);
            }
            focusedId = string.Empty;
            message = string.Empty;
        }

        public CommandResult Choose(ActivityId activity, QueueMode mode)
        {
            AvailableAction offer;
            if (!TryFindOffer(activity, out offer))
            {
                return Refuse(RejectReason.InvalidParameters);
            }
            // The offer and the opened object must agree; the menu may only
            // command this object.
            if (offer.TargetId.HasValue && offer.TargetId.Value != (long)target)
            {
                return Refuse(RejectReason.InvalidParameters);
            }
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record))
            {
                return Refuse(RejectReason.InvalidParameters);
            }
            CommandResult result = commands.Submit(new CommandEnvelope(
                (CommandId)(nextCommandId++),
                world.Epoch,
                world.GameSeconds,
                CommandSource.PlayerHuman,
                record.ResidentSequence + 1u,
                CommandKind.DirectActivity,
                resident,
                activity,
                (long)target,
                new CommandParameters.Direct(mode)));
            message = result.Accepted
                ? "Accepted " + activity.Code + " " + mode
                : result.Reason.ToString();
            return result;
        }

        public IReadOnlyList<HudElement> Elements(int width, int height)
        {
            List<HudElement> elements = new List<HudElement>(activities.Count * 2);
            int order = 0;
            for (int i = 0; i < activities.Count; i++)
            {
                Add(elements, activities[i].Activity, QueueMode.Immediate, width, order++);
                Add(elements, activities[i].Activity, QueueMode.Append, width, order++);
            }
            return elements.AsReadOnly();
        }

        public bool TryInput(string binding)
        {
            string action = accessibility.ResolveInput(binding);
            if (action.Length == 0 || input.RequiresHold(action))
            {
                return false;
            }
            if (action == "SelectNext")
            {
                return Move(1);
            }
            if (action == "SelectPrevious")
            {
                return Move(-1);
            }
            if (action != "Confirm" || focusedId.Length == 0)
            {
                return false;
            }
            ActivityId activity;
            QueueMode mode;
            if (!TryParseChoice(focusedId, out activity, out mode))
            {
                return false;
            }
            Choose(activity, mode);
            return true;
        }

        public bool TryClick(string elementId)
        {
            ActivityId activity;
            QueueMode mode;
            if (!TryParseChoice(elementId, out activity, out mode))
            {
                return false;
            }
            Choose(activity, mode);
            return true;
        }

        private CommandResult Refuse(RejectReason reason)
        {
            message = reason.ToString();
            return CommandResult.Reject(reason);
        }

        private bool TryFindOffer(ActivityId activity, out AvailableAction offer)
        {
            for (int i = 0; i < activities.Count; i++)
            {
                if (string.Equals(activities[i].Activity.Code, activity.Code,
                    StringComparison.Ordinal))
                {
                    offer = activities[i];
                    return true;
                }
            }
            offer = default(AvailableAction);
            return false;
        }

        private void Add(List<HudElement> elements, ActivityId activity, QueueMode mode,
            int width, int order)
        {
            string id = ChoiceId(activity, mode);
            string text = activity.Code + " " + mode;
            int perRow = Math.Max(1, (width - 16) / 216);
            elements.Add(new HudElement
            {
                Id = id,
                Label = text,
                ShapeId = mode == QueueMode.Immediate ? "action-immediate" : "action-append",
                HoverInfo = text,
                Appearance = "Enabled",
                Focusable = true,
                HasPointerPath = true,
                RevealOnClick = true,
                RevealOnFocus = true,
                ReadingOrder = order,
                ForegroundRgb = 0x000000,
                BackgroundRgb = 0xFFFFFF,
                X = 16f + (order % perRow) * 216f,
                Y = 16f + (order / perRow) * 56f,
                Width = 200f,
                Height = 48f,
                BaseTextSize = 16f,
                TextSize = accessibility.ScaledTextSize(16f),
            });
        }

        private bool Move(int step)
        {
            List<HudElement> focus = Focusables();
            if (focus.Count == 0)
            {
                return false;
            }
            int index = -1;
            for (int i = 0; i < focus.Count; i++)
            {
                if (focus[i].Id == focusedId)
                {
                    index = i;
                    break;
                }
            }
            index = ((index + step) % focus.Count + focus.Count) % focus.Count;
            focusedId = focus[index].Id;
            return true;
        }

        private List<HudElement> Focusables()
        {
            List<HudElement> focus = new List<HudElement>();
            foreach (HudElement e in Elements(1920, 1080))
            {
                if (e.Focusable)
                {
                    focus.Add(e);
                }
            }
            focus.Sort((a, b) => a.ReadingOrder.CompareTo(b.ReadingOrder));
            return focus;
        }

        private bool TryParseChoice(string elementId, out ActivityId activity,
            out QueueMode mode)
        {
            for (int i = 0; i < activities.Count; i++)
            {
                ActivityId candidate = activities[i].Activity;
                if (ChoiceId(candidate, QueueMode.Immediate) == elementId)
                {
                    activity = candidate;
                    mode = QueueMode.Immediate;
                    return true;
                }
                if (ChoiceId(candidate, QueueMode.Append) == elementId)
                {
                    activity = candidate;
                    mode = QueueMode.Append;
                    return true;
                }
            }
            activity = default(ActivityId);
            mode = default(QueueMode);
            return false;
        }

        private static string ChoiceId(ActivityId activity, QueueMode mode)
        {
            return "action:" + activity.Code + ":" + mode;
        }
    }
}
