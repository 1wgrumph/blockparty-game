// SAM-LOOK (owner, 2026-11-04): the Look tool's HUD. A "Look" button left of
// the corner buttons, the typed box with the SAM disclosure, and the SAM
// outlines with a label on the best one while the camera holds the capture
// pose. Presentation only: Enter calls SamLook.Ask, the same entry as voice.
using Blockparty.Presentation;
using UnityEngine;

public sealed partial class AlphaHud
{
    public sealed partial class Frame
    {
        public SamLook Look;
        public bool LookFresh;      // the camera still holds the capture pose
        public System.Action OpenLook;
    }

    string lookLayoutKey = "";

    // INTEGRATE-W5: left edge of the Look slot, so the Gazette button (drawn after the HUD) sits left of it.
    public float LookSlotLeft { get { return CornerRect(4).x; } }

    void DrawLook(Frame f)
    {
        SamLook look = f.Look;
        if (look == null) return;
        if (f.LookFresh) DrawLookOutlines(look);

        Rect b = CornerRect(4);
        Panel(look.Open ? "iconBtnOn" : "iconBtn", b);
        FitText(new Rect(b.x + U(4), b.y, b.width - U(8), b.height), "Look", 15, look.Open ? White : Denim, true, TextAnchor.MiddleCenter);
        if (Hit(b, "Look for something in view (G). " + SamLook.Disclosure) && f.OpenLook != null) f.OpenLook();
        string layout = "ALPHA look layout w=" + Screen.width + " h=" + Screen.height + " button=" + Centre(b);

        if (look.Open && !f.SettingsOpen)
        {
            float w = Mathf.Min(U(620), Screen.width - U(80));
            Rect card = new Rect((Screen.width - w) / 2f, U(110), w, U(150));
            Panel("panel", card);
            Text(new Rect(card.x + U(20), card.y + U(12), w - U(40), U(28)), "Look for...", 18, Ink);
            FitText(new Rect(card.x + U(20), card.y + U(40), w - U(40), U(22)), SamLook.Disclosure, 13, Denim, false, TextAnchor.MiddleLeft);
            float goW = Measure("Look", 16) + U(40);
            Rect field = new Rect(card.x + U(20), card.y + U(72), w - U(52) - goW, U(46));
            Rect go = new Rect(field.xMax + U(12), field.y, goW, field.height);
            Panel("opt", field, false);
            GUIStyle style = new GUIStyle(Style(16, Ink, false, TextAnchor.MiddleLeft))
            { padding = new RectOffset(Mathf.RoundToInt(U(14)), Mathf.RoundToInt(U(14)), 0, 0), clipping = TextClipping.Clip };
            Event e = Event.current;
            // Enter and Escape before the field, which would otherwise swallow them.
            if (e.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == "samLook")
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { look.Ask(look.Draft); e.Use(); }
                else if (e.keyCode == KeyCode.Escape) { look.Open = false; e.Use(); }
            }
            GUI.SetNextControlName("samLook");
            look.Draft = GUI.TextField(field, look.Draft ?? "", 60, style);
            if (look.Open) GUI.FocusControl("samLook");
            if (look.Draft.Length == 0)
                Text(new Rect(field.x + U(14), field.y, field.width - U(28), field.height), "the woman in the red shirt, a bike, the cafe...", 15, Denim, false);
            Panel(Hover(go) ? "optHover" : "opt", go, false);
            FitText(go, "Look", 16, Ink, true, TextAnchor.MiddleCenter);
            if (Hit(go, "Look (Enter)")) look.Ask(look.Draft);
            layout += " field=" + Centre(field) + " go=" + Centre(go);
        }
        if (LookRepaint() && layout != lookLayoutKey) { lookLayoutKey = layout; Debug.Log(layout); }
    }

    static bool LookRepaint() { return Event.current.type == EventType.Repaint; }

    void DrawLookOutlines(SamLook look)
    {
        if (!LookRepaint() || look.ImageWidth <= 0 || look.ImageHeight <= 0) return;
        float sx = Screen.width / (float)look.ImageWidth, sy = Screen.height / (float)look.ImageHeight;
        for (int i = 0; i < look.Outlines.Count; i++)
        {
            var poly = look.Outlines[i].Polygon;
            bool best = i == look.Best;
            Color c = best ? Gold : A(Ivory, 0.85f);
            float t = best ? U(4) : U(2);
            for (int p = 0; p < poly.Count; p++)
            {
                var a = poly[p];
                var z = poly[(p + 1) % poly.Count];
                LookLine(new Vector2(a.X * sx, a.Y * sy), new Vector2(z.X * sx, z.Y * sy), c, t);
            }
        }
        if (look.Best < 0 || look.Best >= look.Outlines.Count) return;
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var p in look.Outlines[look.Best].Polygon)
        {
            x0 = Mathf.Min(x0, p.X * sx); x1 = Mathf.Max(x1, p.X * sx);
            y0 = Mathf.Min(y0, p.Y * sy); y1 = Mathf.Max(y1, p.Y * sy);
        }
        string name = look.TargetName.Length > 0 ? look.TargetName : look.Phrase;
        float w = Measure(name, 15) + U(24), h = U(28);
        // Below a resident, whose selected world tag sits above them; above a place.
        float ty = look.Kind == LookKind.Resident ? Mathf.Min(y1 + U(6), Screen.height - h - U(4)) : Mathf.Max(U(4), y0 - h - U(6));
        Rect tag = new Rect(Mathf.Clamp((x0 + x1) / 2f - w / 2f, U(4), Screen.width - w - U(4)), ty, w, h);
        Panel("nameTag", tag, false);
        Text(tag, name, 15, Gold, true, TextAnchor.MiddleCenter);
    }

    static void LookLine(Vector2 a, Vector2 b, Color color, float width)
    {
        Vector2 d = b - a;
        float len = d.magnitude;
        if (len < 0.5f) return;
        Matrix4x4 m = GUI.matrix;
        GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, a);
        GUI.DrawTexture(new Rect(a.x, a.y - width / 2f, len, width), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, color, 0, 0);
        GUI.matrix = m;
    }
}
