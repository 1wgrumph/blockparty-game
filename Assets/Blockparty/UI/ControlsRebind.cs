// CONTROLS-LOGO (M5-OD-36): state of the Settings > Controls page over the one
// Accessibility binding table. Pick an action, press a key: a key another
// action (or a fixed live key) already uses is refused with its owner named and
// capture stays open; Escape cancels. Reset restores every default binding
// through TryRemap only, so Accessibility.cs keeps its GATE-M2-pinned digest.
// Plain C# 9, no engine reference; all state on the instance.
using System;
using System.Text;
using Blockparty.Contracts;

namespace Blockparty.UI
{
    public sealed class ControlsRebind
    {
        // Keys AlphaDirector.HandleInput reads directly; an action bound to one would fire both.
        public static readonly string[] FixedKeys = { "J", "K", "L", "P", "F5", "F9", "F10" };
        static readonly string[] FixedNames = { "Conceive", "Move household", "Rest", "Putt", "Save", "Reload", "Quit" };

        static readonly string[] Labels =
        {
            "Pan up", "Pan down", "Pan left", "Pan right",
            "Zoom in", "Zoom out", "Rotate left", "Rotate right",
            "Next resident", "Previous resident", "Open action menu", "Close action menu",
            "Confirm", "Cancel action", "Speed up", "Slow down", "Pause", "Open map",
            "Settings", "Do it now", "Add to queue", "Inspect"
        };

        readonly Accessibility access;
        readonly Action changed;
        readonly Action<string> log;

        public ControlsRebind(Accessibility access, Action changed, Action<string> log)
        {
            this.access = access ?? throw new ArgumentNullException(nameof(access));
            this.changed = changed ?? delegate { };
            this.log = log ?? delegate { };
        }

        public bool Open { get; private set; }
        public int Focus { get; private set; }
        public string Capturing { get; private set; }   // action awaiting a key, or null
        public string Message { get; private set; } = "";
        public bool MessageIsError { get; private set; }

        // Rows: every action, the hold-to-talk key, then Reset to defaults, then Back.
        public static int ActionCount { get { return InputActions.Names.Length; } }
        public static int TalkRowIndex { get { return ActionCount; } }
        public static int ResetRow { get { return ActionCount + 1; } }
        public static int BackRow { get { return ActionCount + 2; } }
        public static int RowCount { get { return ActionCount + 3; } }

        // VOICE-LIVE (M5-OD-38): the talk row starts the voice talk-key capture,
        // which keeps its own key table and refusal rules; Tell shows its result.
        public Action TalkRow;

        public void Tell(string text, bool error) { Say(text ?? "", error); }

        public static string Label(string action)
        {
            int i = Array.IndexOf(InputActions.Names, action);
            return i < 0 ? action ?? "" : Labels[i];
        }

        public string Binding(string action) { return Display(access.BindingFor(action)); }

        public int ChangedCount()
        {
            int n = 0;
            foreach (string action in InputActions.Names)
                if (access.BindingFor(action) != InputActions.DefaultBinding(action)) n++;
            return n;
        }

        public void Show()
        {
            Open = true;
            Focus = 0;
            Capturing = null;
            Say("", false);
            log("open changed=" + ChangedCount());
        }

        public void Hide()
        {
            if (!Open) return;
            Open = false;
            Capturing = null;
            log("close");
        }

        public void Move(int step)
        {
            Focus = ((Focus + step) % RowCount + RowCount) % RowCount;
            log("focus row=" + Focus);
        }

        // Keyboard Confirm or a click on a row.
        public void Activate(int row)
        {
            Focus = row;
            if (row == ResetRow) ResetDefaults();
            else if (row == BackRow) Hide();
            else if (row == TalkRowIndex) { Capturing = null; Say("", false); if (TalkRow != null) TalkRow(); }
            else if (row >= 0 && row < ActionCount) Begin(InputActions.Names[row]);
        }

        public void Begin(string action)
        {
            if (!InputActions.IsKnown(action)) return;
            Capturing = action;
            Say("Press a new key for " + Label(action) + ". Esc cancels.", false);
            log("capture action=" + action);
        }

        // A key name as the live player reads it (UnityEngine.KeyCode names). True when a binding changed.
        public bool Press(string key)
        {
            string action = Capturing;
            if (action == null || string.IsNullOrWhiteSpace(key)) return false;
            if (key == "Escape")
            {
                Capturing = null;
                Say(Label(action) + " stays " + Binding(action) + ".", false);
                log("cancel action=" + action);
                return false;
            }
            int fixedIndex = Array.IndexOf(FixedKeys, key);
            string owner = fixedIndex >= 0 ? FixedNames[fixedIndex] : access.ResolveInput(key);
            if (fixedIndex >= 0 || (owner.Length > 0 && owner != action))
            {
                string name = fixedIndex >= 0 ? owner : Label(owner);
                Say(Display(key) + " is already used by " + name + ". Press another key, or Esc.", true);
                log("conflict action=" + action + " key=" + key + " owner=" + (fixedIndex >= 0 ? "fixed:" + owner : owner));
                return false;
            }
            if (!access.TryRemap(action, key))
            {
                Say(Display(key) + " cannot be used. Press another key, or Esc.", true);
                log("refused action=" + action + " key=" + key);
                return false;
            }
            Capturing = null;
            Say(Label(action) + " is now " + Display(key) + ".", false);
            log("remap action=" + action + " key=" + key);
            changed();
            return true;
        }

        // Two passes through TryRemap: park every changed action on a unique
        // placeholder, then restore its default (defaults never collide).
        public int ResetDefaults()
        {
            Capturing = null;
            string[] names = InputActions.Names;
            bool[] moved = new bool[names.Length];
            int n = 0;
            for (int i = 0; i < names.Length; i++)
            {
                if (access.BindingFor(names[i]) == InputActions.DefaultBinding(names[i])) continue;
                moved[i] = access.TryRemap(names[i], "ResetParked" + i);
                if (moved[i]) n++;
            }
            for (int i = 0; i < names.Length; i++)
                if (moved[i]) access.TryRemap(names[i], InputActions.DefaultBinding(names[i]));
            Say(n == 0 ? "All keys are already the defaults." : "All keys reset to the defaults.", false);
            log("reset changed=" + n);
            if (n > 0) changed();
            return n;
        }

        void Say(string text, bool error)
        {
            Message = text;
            MessageIsError = error;
        }

        // Player-facing key name: "Return" -> "Enter", "Alpha1" -> "1", "PageUp" -> "Page Up".
        public static string Display(string key)
        {
            if (string.IsNullOrEmpty(key)) return "—";
            switch (key)
            {
                case "Return": return "Enter";
                case "Escape": return "Esc";
                case "KeypadEnter": return "Num Enter";
                case "BackQuote": return "`";
                case "Minus": return "-";
                case "Equals": return "=";
                case "LeftBracket": return "[";
                case "RightBracket": return "]";
                case "Semicolon": return ";";
                case "Quote": return "'";
                case "Comma": return ",";
                case "Period": return ".";
                case "Slash": return "/";
                case "Backslash": return "\\";
            }
            if (key.Length == 6 && key.StartsWith("Alpha", StringComparison.Ordinal)) return key.Substring(5);
            if (key.StartsWith("Keypad", StringComparison.Ordinal)) key = "Num" + key.Substring(6);
            StringBuilder b = new StringBuilder(key.Length + 4);
            for (int i = 0; i < key.Length; i++)
            {
                if (i > 0 && char.IsUpper(key[i]) && char.IsLower(key[i - 1])) b.Append(' ');
                else if (i > 0 && char.IsDigit(key[i]) && char.IsLetter(key[i - 1]) && key[0] != 'F') b.Append(' ');
                b.Append(key[i]);
            }
            return b.ToString();
        }
    }
}
