using System.Collections.Generic;
using Blockparty.AI;
using UnityEngine;

// GAZETTE page drawing (IMGUI, the HUD palette): a cartoony newspaper sheet over
// a dimmed game with masthead, dateline, headline, the front-page picture in a
// newsprint frame, the story, the gossip column and the credit line.
public sealed class GazetteView
{
    static readonly Color Paper = Hex(0xF4EBDD), Ink = Hex(0x243238), Rust = Hex(0xAD5737),
        RustDark = Hex(0x8F4228), Rule = Hex(0x6B6158), Inset = Hex(0xE2D6C3), Gold = Hex(0xF2B24C),
        Sepia = new Color(1f, 0.95f, 0.84f, 1f);

    Texture2D white;
    GUIStyle label;
    Font font;
    float k = 1f;

    public void Draw(GazettePage page, IList<GazetteFact> facts, Texture2D picture, string source, string credit,
        string dateline, bool busy, float textScale)
    {
        Setup();
        k = Mathf.Min(Screen.width / 1280f, Screen.height / 720f) * Mathf.Clamp(textScale <= 0f ? 1f : textScale, 0.8f, 1.6f);
        GUI.depth = -100;
        Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(Ink.r, Ink.g, Ink.b, 0.55f));
        float w = Mathf.Min(Screen.width * 0.86f, 1120f * k), h = Mathf.Min(Screen.height * 0.9f, 660f * k);
        Rect sheet = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
        Panel(new Rect(sheet.x + 8 * k, sheet.y + 10 * k, w, h), new Color(0f, 0f, 0f, 0.35f));
        Panel(new Rect(sheet.x - 4 * k, sheet.y - 4 * k, w + 8 * k, h + 8 * k), Ink);
        Panel(sheet, Paper);

        float pad = 28 * k, x = sheet.x + pad, y = sheet.y + 16 * k, cw = w - 2 * pad;
        Text(new Rect(x, y, cw, 58 * k), "THE BLOCK GAZETTE", 46, Ink, true, TextAnchor.MiddleCenter);
        y += 58 * k;
        Fill(new Rect(x, y, cw, 3 * k), Ink);
        Fill(new Rect(x, y + 5 * k, cw, 1 * k), Ink);
        Text(new Rect(x, y + 7 * k, cw, 22 * k), dateline, 14, Rule, false, TextAnchor.MiddleLeft);
        Text(new Rect(x, y + 7 * k, cw, 22 * k), "Price: one friendly wave", 14, Rule, false, TextAnchor.MiddleRight);
        y += 30 * k;
        Fill(new Rect(x, y, cw, 1 * k), Ink);
        y += 8 * k;
        float headH = Height(page.Headline, cw, 34, true);
        Text(new Rect(x, y, cw, headH), page.Headline, 34, Ink, true, TextAnchor.UpperLeft, true);
        y += headH + 8 * k;

        float footer = 54 * k, bodyH = sheet.yMax - footer - y;
        float leftW = cw * 0.52f, gap = 22 * k, rightX = x + leftW + gap, rightW = cw - leftW - gap;
        // Front-page picture in a newsprint frame.
        Rect frame = new Rect(x, y, leftW, Mathf.Min(bodyH - 30 * k, leftW * 0.68f));
        Fill(frame, Ink);
        Rect mat = new Rect(frame.x + 4 * k, frame.y + 4 * k, frame.width - 8 * k, frame.height - 8 * k);
        Fill(mat, source == "muse" ? Color.white : Inset);
        if (picture != null)
        {
            Color old = GUI.color;
            if (source != "muse") GUI.color = Sepia;
            GUI.DrawTexture(new Rect(mat.x + 6 * k, mat.y + 6 * k, mat.width - 12 * k, mat.height - 12 * k), picture, ScaleMode.ScaleToFit);
            GUI.color = old;
        }
        GazetteFact lead = GazetteWriter.Lead(facts);
        string caption = (lead != null ? lead.Name : "The block") + (source == "muse" ? ", as drawn by our illustrator."
            : source == "sam" ? ", snapped by our photographer." : ", on the block yesterday.");
        Text(new Rect(x, frame.yMax + 4 * k, leftW, 22 * k), caption, 13, Rule, false, TextAnchor.UpperLeft);

        // Story, then the gossip column.
        float sy = y;
        float storyH = Height(page.Story, rightW, 17, false);
        Text(new Rect(rightX, sy, rightW, storyH), page.Story, 17, Ink, false, TextAnchor.UpperLeft, true);
        sy += storyH + 14 * k;
        Panel(new Rect(rightX, sy, rightW, 28 * k), RustDark);   // Paper on RustDark 5.97:1 (Rust was 4.24:1)
        Text(new Rect(rightX + 10 * k, sy, rightW - 20 * k, 28 * k), "GOSSIP COLUMN", 16, Paper, true, TextAnchor.MiddleLeft);
        sy += 34 * k;
        foreach (string g in page.Gossip)
        {
            float gh = Height("• " + g, rightW, 15, false);
            if (sy + gh > sheet.yMax - footer) break;
            Text(new Rect(rightX, sy, rightW, gh), "• " + g, 15, RustDark, false, TextAnchor.UpperLeft, true);
            sy += gh + 6 * k;
        }

        // Footer: credit line and close hint.
        float fy = sheet.yMax - footer + 8 * k;
        Fill(new Rect(x, fy, cw, 1 * k), Ink);
        Text(new Rect(x, fy + 6 * k, cw * 0.62f, 22 * k), credit, 13, Ink, false, TextAnchor.MiddleLeft);
        Text(new Rect(x, fy + 26 * k, cw * 0.62f, 18 * k), busy ? "The presses are still rolling…" : "", 12, RustDark, false, TextAnchor.MiddleLeft);
        Rect hint = new Rect(x + cw - 250 * k, fy + 8 * k, 250 * k, 32 * k);
        Panel(hint, Gold);
        Text(hint, "Any key or click to close", 14, Ink, true, TextAnchor.MiddleCenter);
    }

    // HUD "Gazette" button in the free top-bar slot left of the menu icons; true when clicked.
    public bool Button(float right)
    {
        Setup();
        k = Mathf.Min(Screen.width / 1280f, Screen.height / 720f);
        // INTEGRATE-W5: never over the HUD Look button (sam-look), whose slot ends at `right`.
        Rect r = new Rect(Mathf.Min(Screen.width - 325f * k, right - 118f * k), 20f * k, 110f * k, 42f * k);
        bool hover = r.Contains(Event.current.mousePosition);
        Panel(new Rect(r.x, r.y + 3f * k, r.width, r.height), new Color(0f, 0f, 0f, 0.18f));
        Panel(r, hover ? Hex(0xFFF4D4) : Paper);
        Text(r, "Gazette (Y)", 15, Ink, true, TextAnchor.MiddleCenter);
        return GUI.Button(r, GUIContent.none, GUIStyle.none);
    }

    void Setup()
    {
        if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (label == null) label = new GUIStyle { font = font, clipping = TextClipping.Clip, richText = false };
        if (white == null) { white = new Texture2D(1, 1); white.SetPixel(0, 0, Color.white); white.Apply(); }
    }

    GUIStyle Style(float size, Color color, bool bold, TextAnchor anchor, bool wrap)
    {
        label.fontSize = Mathf.Max(8, Mathf.RoundToInt(size * k));
        label.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        label.alignment = anchor;
        label.wordWrap = wrap;
        label.normal.textColor = color;
        return label;
    }

    void Text(Rect r, string s, float size, Color color, bool bold, TextAnchor anchor, bool wrap = false)
    {
        GUI.Label(r, s ?? "", Style(size, color, bold, anchor, wrap));
    }

    float Height(string s, float width, float size, bool bold)
    {
        return Style(size, Ink, bold, TextAnchor.UpperLeft, true).CalcHeight(new GUIContent(s ?? ""), width);
    }

    void Fill(Rect r, Color c)
    {
        Color old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, white);
        GUI.color = old;
    }

    // Rounded panel (IMGUI border radius).
    void Panel(Rect r, Color c)
    {
        GUI.DrawTexture(r, white, ScaleMode.StretchToFill, true, 0f, c, 0f, 12f * k);
    }

    static Color Hex(int rgb)
    {
        return new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);
    }
}
