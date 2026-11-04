using System.Collections.Generic;
using System.Text;
using Blockparty.AI;
using UnityEngine;

// VOICE-LIVE (M5-OD-38): the mic indicator with the heard text, and the
// Settings > Voice page (talk key, Hey Meta activator, custom phrases), drawn
// with the AlphaHud panels and palette. The page is modal like Controls.
public sealed partial class AlphaHud
{
    public sealed partial class Frame
    {
        public AlphaVoice Voice;
    }

    const int VoiceListMax = 6;
    string voiceLayoutKey;
    GUIStyle voiceField;

    // Above the bottom row, centred: shown while listening or sending, for a few
    // seconds after a result, and always (compact) while the activator is on.
    void DrawVoiceIndicator(Frame f, float rowTop)
    {
        AlphaVoice v = f.Voice;
        if (v == null) return;
        IReadOnlyList<string> choices = v.Choices;
        if (choices.Count > 0) { DrawVoiceChoices(f, choices, rowTop); return; }
        bool recent = Time.unscaledTime < v.ShownUntil;
        string conflict = v.TalkKeyConflict();
        string text;
        if (v.Listening || v.Busy || recent)
            text = (v.Heard.Length > 0 && !v.Listening && !v.Busy && !v.AwaitingCommand ? "Heard: “" + v.Heard + "” — " : "") + v.Status;
        else if (conflict.Length > 0) text = "Talk key " + v.Settings.TalkKey + " is also " + conflict + "; change it in Settings > Voice.";
        else if (v.Settings.Activator) text = "Mic on · say “Hey Meta”, then a command";
        else return;
        float h = U(44), w = Mathf.Min(Measure(text, 15) + U(70), Screen.width - U(40));
        Rect r = new Rect((Screen.width - w) / 2f, rowTop - U(16) - h, w, h);
        Panel("bubble", r);
        bool listening = v.Listening || v.AwaitingCommand;   // META-LIVE: the follow-up window after "Hey Meta"
        Color dot = listening ? Coral : v.Busy ? Gold : Teal;
        float pulse = listening ? 0.6f + 0.4f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 4f)) : 1f;
        DrawIcon(new Rect(r.x + U(16), r.center.y - U(9), U(18), U(18)), Icon.Disc, A(dot, pulse));
        FitText(new Rect(r.x + U(44), r.y, w - U(58), h), text, 15, Ink, true, TextAnchor.MiddleLeft);
        if (Hover(r)) tooltip = "Hold " + v.Settings.TalkKey + " to talk" + (v.Settings.Activator ? " · Hey Meta is on" : "");
    }

    // An ambiguous transcript: one button per meaning (keys 1-9 too) and a
    // dismiss button. Nothing runs until the player picks one.
    void DrawVoiceChoices(Frame f, IReadOnlyList<string> choices, float rowTop)
    {
        AlphaVoice v = f.Voice;
        string head = (v.Heard.Length > 0 ? "Heard “" + v.Heard + "” — " : "") + "which one?";
        float rowH = U(44), w = Mathf.Min(U(460), Screen.width - U(40));
        foreach (string c in choices) w = Mathf.Min(Mathf.Max(w, Measure("9  " + c, 15) + U(60)), Screen.width - U(40));
        float h = U(14) + U(36) + (choices.Count + 1) * (rowH + U(6)) + U(8);
        Rect r = new Rect((Screen.width - w) / 2f, Mathf.Max(U(8), rowTop - U(16) - h), w, h);
        Panel("panel", r);
        DrawIcon(new Rect(r.x + U(16), r.y + U(14) + U(9), U(18), U(18)), Icon.Disc, Gold);
        FitText(new Rect(r.x + U(44), r.y + U(14), w - U(58), U(36)), head, 15, Ink, true, TextAnchor.MiddleLeft);
        StringBuilder layout = new StringBuilder("ALPHA voice choices w=" + Screen.width + " h=" + Screen.height + " count=" + choices.Count);
        float y = r.y + U(14) + U(36);
        for (int i = 0; i <= choices.Count; i++)
        {
            bool dismiss = i == choices.Count;
            Rect row = new Rect(r.x + U(14), y, w - U(28), rowH);
            Panel(Hover(row) ? "optHover" : "opt", row, false);
            string label = dismiss ? "None of these (use the controls)" : (i < 9 ? (i + 1) + "  " : "") + choices[i];
            FitText(new Rect(row.x + U(14), row.y, row.width - U(28), row.height), label, 15, dismiss ? Denim : Ink, !dismiss, TextAnchor.MiddleLeft);
            if (Hit(row, dismiss ? "Run nothing" : "Run: " + choices[i])) { if (dismiss) v.DismissChoices(); else v.Choose(i); }
            layout.Append(dismiss ? " vdismiss=" : " vchoice" + i + "=").Append(Centre(row));
            y += rowH + U(6);
        }
        string key = layout.ToString();
        if (Event.current.type == EventType.Repaint && key != voiceChoicesKey) { voiceChoicesKey = key; Debug.Log(key + " meanings=\"" + string.Join("|", choices) + "\""); }
    }

    string voiceChoicesKey;

    Rect VoiceCard(int listRows)
    {
        float w = Mathf.Min(U(760), Screen.width - U(24));
        float h = U(18) + U(44) + 2 * U(52) + U(30) + U(36) + listRows * U(40) + U(52) + U(36) + U(56) + U(18);
        return new Rect((Screen.width - w) / 2f, Mathf.Max(U(8), (Screen.height - h) / 2f), w, h);
    }

    void DrawVoicePage(Frame f)
    {
        AlphaVoice v = f.Voice;
        if (Event.current.type == EventType.Repaint)
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, A(Ink, 0.5f), 0, 0);
        Block(new Rect(0, 0, Screen.width, Screen.height));
        List<KeyValuePair<string, string>> aliases = new List<KeyValuePair<string, string>>(v.Settings.Aliases.Entries);
        int shown = Mathf.Min(aliases.Count, VoiceListMax);
        Rect card = VoiceCard(Mathf.Max(1, shown) + (aliases.Count > VoiceListMax ? 1 : 0));
        Panel("panel", card);
        float x = card.x + U(24), w = card.width - U(48), y = card.y + U(18);
        DrawIcon(new Rect(x, y + U(6), U(28), U(28)), Icon.Gear, Teal);
        Text(new Rect(x + U(38), y, w - U(38), U(40)), "Voice", 22, Ink);
        y += U(44);

        StringBuilder layout = new StringBuilder("ALPHA voice layout w=" + Screen.width + " h=" + Screen.height);
        string[] names = { "Talk key (hold to speak)", "“Hey Meta” activator" };
        string[] values = { v.Capturing ? "Press a key" : AlphaVoice.Display(v.Settings.TalkKey), v.Settings.Activator ? "On" : "Off" };
        bool[] on = { v.Capturing, v.Settings.Activator };
        for (int i = 0; i < 2; i++)
        {
            Rect row = new Rect(x, y, w, U(46));
            VoiceRow(f, row, i, names[i], values[i], on[i]);
            layout.Append(" vrow" + i + "=" + Centre(row));
            y += U(52);
        }
        FitText(new Rect(x + U(14), y - U(4), w - U(28), U(30)), "While on, the microphone listens and speech segments are sent to Muse Voice.", 13, Denim, false, TextAnchor.MiddleLeft);
        y += U(30);
        Text(new Rect(x, y, w, U(36)), "Custom phrases", 17, Ink);
        y += U(36);
        if (aliases.Count == 0)
        {
            Text(new Rect(x + U(14), y, w, U(40)), "None yet. Add one below, for example “chill out” for pause.", 14, Denim, false);
            y += U(40);
        }
        for (int i = 0; i < shown; i++)
        {
            Rect row = new Rect(x, y, w, U(36));
            Panel("opt", row, false);
            v.Settings.Aliases.Inactive.TryGetValue(aliases[i].Key, out string off);
            FitText(new Rect(row.x + U(14), row.y, w - U(70), row.height), "“" + aliases[i].Key + "”  →  " + aliases[i].Value
                + (off != null ? "  ·  off: " + off : ""), 15, off != null ? RustDark : Ink, true, TextAnchor.MiddleLeft);
            if (off != null && Hover(row)) tooltip = off;
            Rect del = new Rect(row.xMax - U(44), row.y + U(3), U(30), U(30));
            Panel(Hover(del) ? "optHover" : "spd", del, false);
            DrawIcon(new Rect(del.x + U(9), del.y + U(9), U(12), U(12)), Icon.Close, Denim);
            if (Hit(del, "Remove “" + aliases[i].Key + "”")) v.RemoveAlias(aliases[i].Key);
            layout.Append(" vdel" + i + "=" + Centre(del));
            y += U(40);
        }
        if (aliases.Count > VoiceListMax)
        {
            Text(new Rect(x + U(14), y, w, U(40)), "+" + (aliases.Count - VoiceListMax) + " more (remove one to see it)", 14, Denim, false);
            y += U(40);
        }

        // Add row: phrase field, command chip (row 2), Add (row 3).
        float addW = Measure("Add", 17) + U(48);
        float cmdW = Mathf.Max(Measure("set speed normal", 15), Measure("stop following", 15)) + U(40);
        Rect field = new Rect(x, y, w - cmdW - addW - U(24), U(46));
        Rect cmd = new Rect(field.xMax + U(12), y, cmdW, U(46));
        Rect add = new Rect(cmd.xMax + U(12), y, addW, U(46));
        Panel("opt", field, false);
        if (voiceField == null || voiceField.fontSize != Style(16, Ink, false, TextAnchor.MiddleLeft).fontSize)
            voiceField = new GUIStyle(Style(16, Ink, false, TextAnchor.MiddleLeft)) { padding = new RectOffset(Mathf.RoundToInt(U(14)), Mathf.RoundToInt(U(14)), 0, 0), clipping = TextClipping.Clip };
        voiceField.normal.textColor = Ink;
        GUI.SetNextControlName("voicePhrase");
        v.Draft = GUI.TextField(field, v.Draft ?? "", 40, voiceField);
        if (v.Draft.Length == 0 && GUI.GetNameOfFocusedControl() != "voicePhrase")
            Text(new Rect(field.x + U(14), field.y, field.width - U(28), field.height), "Type a phrase…", 15, Denim, false);
        if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return && GUI.GetNameOfFocusedControl() == "voicePhrase")
        { v.AddDraft(); Event.current.Use(); }
        VoiceChip(f, cmd, 2, VoiceAliases.Commands[v.DraftCommand], "Command (click to change)");
        VoiceChip(f, add, 3, "Add", "Add this phrase");
        layout.Append(" vfield=" + Centre(field) + " vcmd=" + Centre(cmd) + " vadd=" + Centre(add));
        y += U(52);

        if (v.PageMessage.Length > 0)
            FitText(new Rect(x + U(4), y, w - U(8), U(36)), v.PageMessage, 14, v.PageMessageIsError ? RustDark : OliveDark, true, TextAnchor.MiddleLeft);
        y += U(36);
        float backW = Measure("Back", 17) + U(56);
        Rect back = new Rect(card.xMax - U(24) - backW, y + U(6), backW, U(44));
        VoiceChip(f, back, 4, "Back", "Back to Settings (Escape)");
        layout.Append(" vback=" + Centre(back));

        string key = layout.ToString();
        if (Event.current.type == EventType.Repaint && key != voiceLayoutKey) { voiceLayoutKey = key; Debug.Log(key); }
    }

    void VoiceRow(Frame f, Rect row, int index, string name, string value, bool on)
    {
        AlphaVoice v = f.Voice;
        Panel(v.Focus == index ? "optHi" : Hover(row) ? "optHover" : "opt", row, false);
        float chipW = Mathf.Max(U(106), Measure(value, 14) + U(24));
        Rect chip = new Rect(row.xMax - chipW - U(10), row.y + U(9), chipW, U(28));
        FitText(new Rect(row.x + U(14), row.y, chip.x - row.x - U(20), row.height), name, 16, Ink, true, TextAnchor.MiddleLeft);
        Panel(on ? "spdOn" : "spd", chip, false);
        FitText(new Rect(chip.x + U(4), chip.y, chip.width - U(8), chip.height), value, 14, on ? White : Denim, true, TextAnchor.MiddleCenter);
        if (Hit(row, name + ": " + value + " (click to change)")) { GUIUtility.keyboardControl = 0; v.Activate(index); }
    }

    void VoiceChip(Frame f, Rect r, int index, string label, string tip)
    {
        AlphaVoice v = f.Voice;
        Panel(v.Focus == index ? "optHi" : Hover(r) ? "optHover" : "opt", r, false);
        FitText(new Rect(r.x + U(8), r.y, r.width - U(16), r.height), label, 15, Ink, true, TextAnchor.MiddleCenter);
        if (Hit(r, tip)) { if (index != 3) GUIUtility.keyboardControl = 0; v.Activate(index); }
    }
}
