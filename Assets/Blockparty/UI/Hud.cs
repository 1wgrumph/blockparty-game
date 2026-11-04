// HUD@1: overlay projection over authoritative state (BP-UI-02).
// Portraits, needs, mood, clock/speed, action queue and balance are read
// fresh from WorldState/CommandArbitrator on every Snapshot; the HUD holds
// no StateWriter and writes nothing except cancellations, which go through
// the existing ResidentPanel command contract. Cues are text labels plus
// shape ids, never colour alone; palette pairs meet 4.5:1 contrast and
// text sizes follow Accessibility.ScaledTextSize. Plain C# 9, no engine.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Blockparty.Contracts;
using Blockparty.Simulation;

namespace Blockparty.UI
{
    public sealed class HudElement
    {
        public string Id, Label, ShapeId, HoverInfo, Appearance;
        public bool Focusable, HasPointerPath, RevealOnClick, RevealOnFocus;
        public int ReadingOrder, ForegroundRgb, BackgroundRgb;
        public float X, Y, Width, Height, BaseTextSize, TextSize;
    }

    public sealed class HudPaletteEntry
    {
        public string Appearance;
        public int ForegroundRgb, BackgroundRgb;
    }

    public sealed class HudSnapshot
    {
        public ResidentPanelSnapshot Panel;
        public IEnumerable<ResidentId> Portraits;
        public long GameSeconds, Balance;
        public SimulationSpeed Speed;
        public bool Paused;
        public AgingPreset Aging;
        public string MoodSummary;
        public IEnumerable Elements, Palette;
    }

    public sealed class Hud
    {
        private const int Black = 0x000000, White = 0xFFFFFF;

        private readonly WorldState world;
        private readonly CommandArbitrator commands;
        private readonly Accessibility accessibility;
        private readonly InputMap input;
        private readonly ResidentPanel panel;
        private readonly Narration narration;
        private ResidentId selected;
        private bool hasSelection;
        private int focusIndex = -1;
        private string focusedId = string.Empty;
        private ActivityId lastActivity;
        private CommandResult lastResult;
        private bool hasResult;

        public Hud(WorldState world, CommandArbitrator commands,
            Accessibility accessibility, InputMap input)
        {
            this.world = world;
            this.commands = commands;
            this.accessibility = accessibility;
            this.input = input;
            panel = new ResidentPanel(world, commands);
            narration = new Narration(accessibility, new SpeechDispatcherOutput());
        }

        public Narration Narration { get { return narration; } }
        public string FocusedElementId { get { return focusedId; } }

        public bool Select(ResidentId resident)
        {
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record)) return false;
            selected = resident;
            hasSelection = true;
            narration.Selection(record);
            return true;
        }

        public HudSnapshot Snapshot(int width, int height)
        {
            ResidentPanelSnapshot view;
            string reason;
            if (!hasSelection || !panel.TryInspect(selected, out view, out reason))
                view = new ResidentPanelSnapshot(null, null, new List<ActionEntry>().AsReadOnly());
            ResidentRecord resident = view.Resident;

            var portraits = new List<ResidentId>();
            if (resident != null)
                foreach (ResidentRecord r in world.ResidentsSnapshot())
                    if (r.Household == resident.Household) portraits.Add(r.Id);

            string mood = MoodOf(resident);
            var elements = new List<HudElement>();
            int order = 0;
            foreach (ResidentId id in portraits)
            {
                ResidentRecord p;
                string name = world.TryGetResident(id, out p) ? p.DisplayName : id.ToString();
                elements.Add(Make("portrait:" + (long)id, "Portrait " + name, "portrait",
                    true, ref order, width, elements.Count));
            }
            if (resident != null)
                for (int i = 0; i < StateSchema.NeedCount; i++)
                {
                    Need need = (Need)i;
                    elements.Add(Make("need:" + need,
                        need + " " + resident.GetNeed(need).ToString(CultureInfo.InvariantCulture),
                        "need-bar", true, ref order, width, elements.Count));
                }
            elements.Add(Make("mood", mood, "mood-face", true, ref order, width, elements.Count));
            elements.Add(Make("balance", "Balance " + (resident == null ? 0L : resident.Money),
                "coin", true, ref order, width, elements.Count));
            elements.Add(Make("clock", "Clock " + world.GameSeconds, "clock",
                true, ref order, width, elements.Count));
            Clock clock = commands.Clock;
            elements.Add(Make("speed",
                "Speed " + clock.Speed + (clock.Paused ? " Paused" : " Running") + " Aging " + clock.Preset,
                "speed", true, ref order, width, elements.Count));
            if (view.Current != null)
                elements.Add(CancelElement(view.Current, ref order, width, elements.Count));
            foreach (ActionEntry queued in view.Queue)
                elements.Add(CancelElement(queued, ref order, width, elements.Count));
            HudElement result = Make("result",
                hasResult ? lastActivity + " " + (lastResult.Accepted ? "Accepted" : lastResult.Reason.ToString())
                          : "Result none",
                hasResult ? (lastResult.Accepted ? "result-ok" : "result-blocked") : "result-none",
                false, ref order, width, elements.Count);
            elements.Add(result);

            return new HudSnapshot
            {
                Panel = view,
                Portraits = portraits,
                GameSeconds = world.GameSeconds,
                Balance = resident == null ? 0L : resident.Money,
                Speed = clock.Speed,
                Paused = clock.Paused,
                Aging = clock.Preset,
                MoodSummary = mood,
                Elements = elements,
                Palette = new List<HudPaletteEntry>
                {
                    Pair("Enabled", Black, White), Pair("Disabled", White, Black),
                    Pair("Focused", Black, White), Pair("Selected", White, Black),
                    Pair("Blocked", Black, White),
                },
            };
        }

        public bool TryInput(string activeBinding)
        {
            string action = accessibility.ResolveInput(activeBinding);
            List<HudElement> focus = Focusables();
            if (focus.Count == 0) return false;
            if (action == "SelectNext") return Move(focus, 1);
            if (action == "SelectPrevious") return Move(focus, -1);
            if (action == "Confirm" && focusedId.Length > 0) return TryClick(focusedId);
            return false;
        }

        public bool TryClick(string elementId)
        {
            HudElement target = null;
            foreach (HudElement e in Snapshot(1920, 1080).Elements as List<HudElement>)
                if (e.Id == elementId) target = e;
            if (target == null) return false;
            if (elementId.StartsWith("portrait:", StringComparison.Ordinal))
                return Select((ResidentId)long.Parse(elementId.Substring(9), CultureInfo.InvariantCulture));
            if (elementId.StartsWith("cancel:", StringComparison.Ordinal))
            {
                panel.Cancel(selected, (ActionInstanceId)long.Parse(
                    elementId.Substring(7), CultureInfo.InvariantCulture));
                return true;
            }
            List<HudElement> focus = Focusables();
            for (int i = 0; i < focus.Count; i++)
                if (focus[i].Id == elementId) { focusIndex = i; focusedId = elementId; }
            narration.Focus(target.Label);
            return true;
        }

        public void ActionResult(ResidentId resident, ActivityId activity, CommandResult result)
        {
            lastActivity = activity;
            lastResult = result;
            hasResult = true;
            ResidentRecord record;
            if (world.TryGetResident(resident, out record))
                narration.ActionResult(record, activity, result);
        }

        public void ActionCompleted(ResidentId resident, ActivityId activity)
        {
            ResidentRecord record;
            if (world.TryGetResident(resident, out record))
                narration.ActionCompleted(record, activity);
        }

        private bool Move(List<HudElement> focus, int step)
        {
            focusIndex = ((focusIndex + step) % focus.Count + focus.Count) % focus.Count;
            focusedId = focus[focusIndex].Id;
            narration.Focus(focus[focusIndex].Label);
            return true;
        }

        private List<HudElement> Focusables()
        {
            var list = new List<HudElement>();
            foreach (HudElement e in Snapshot(1920, 1080).Elements as List<HudElement>)
                if (e.Focusable) list.Add(e);
            list.Sort((a, b) => a.ReadingOrder.CompareTo(b.ReadingOrder));
            return list;
        }

        private HudElement CancelElement(ActionEntry entry, ref int order, int width, int index)
        {
            return Make("cancel:" + (long)entry.Action,
                "Cancel " + entry.Activity + " " + (long)entry.Action, "cancel-x",
                true, ref order, width, index);
        }

        private HudElement Make(string id, string label, string shape, bool focusable,
            ref int order, int width, int index)
        {
            const float w = 200f, h = 48f;
            int perRow = Math.Max(1, (width - 16) / 216);
            return new HudElement
            {
                Id = id,
                Label = label,
                ShapeId = shape,
                HoverInfo = label,
                Appearance = "Enabled",
                Focusable = focusable,
                HasPointerPath = true,
                RevealOnClick = true,
                RevealOnFocus = true,
                ReadingOrder = order++,
                ForegroundRgb = Black,
                BackgroundRgb = White,
                X = 16f + (index % perRow) * 216f,
                Y = 16f + (index / perRow) * 56f,
                Width = w,
                Height = h,
                BaseTextSize = 16f,
                TextSize = accessibility.ScaledTextSize(16f),
            };
        }

        private static HudPaletteEntry Pair(string appearance, int fg, int bg)
        {
            return new HudPaletteEntry { Appearance = appearance, ForegroundRgb = fg, BackgroundRgb = bg };
        }

        private string MoodOf(ResidentRecord resident)
        {
            if (resident == null) return "No active feeling";
            var parts = new List<string>();
            foreach (Feeling feeling in resident.Feelings)
                if (feeling.ExpiryGameSeconds > world.GameSeconds)
                    parts.Add(feeling.Kind + " " + feeling.Intensity);
            return parts.Count == 0 ? "No active feeling" : string.Join(", ", parts);
        }
    }
}
