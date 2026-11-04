using System.Collections.Generic;
using Blockparty.UI;
using UnityEngine;

// CONTROLS-PLAY@1: the presentation surface the director drives and
// MeasureProbe reads. Every rectangle and colour recorded in the receipt comes
// from this layout, so a measured sample always names the element that was
// actually drawn. IMGUI only; no product state is owned here.
public sealed class ControlsView : MonoBehaviour
{
    public sealed class ViewRow
    {
        public string Id;
        public string Label;
        public bool Focused;

        public ViewRow(string id, string label, bool focused)
        {
            Id = id;
            Label = label;
            Focused = focused;
        }
    }

    public sealed class ViewElement
    {
        public string Id;
        public string Kind;
        public string Label;
        public Rect Rect;
        public bool Interactive;
        public Color32 Background = new Color32(255, 255, 255, 255);
        public Color32 Foreground = new Color32(0, 0, 0, 255);

        public ViewElement(string id, string kind, Rect rect, bool interactive, string label)
        {
            Id = id;
            Kind = kind;
            Rect = rect;
            Interactive = interactive;
            Label = label;
        }
    }

    public static readonly Color32 Ink = new Color32(0, 0, 0, 255);
    public static readonly Color32 Paper = new Color32(255, 255, 255, 255);
    public static readonly Color32 FocusInk = new Color32(0, 0, 255, 255);
    public static readonly Color32 RowPaper = new Color32(228, 228, 228, 255);
    public static readonly Color32 MutedInk = new Color32(72, 72, 72, 255);

    // Layout regions: banner band, left HUD column, right action menu, and a
    // two-row world-marker dock above the fixed cursor-state row. The menu
    // starts right of the HUD and above the dock, so scaled panels never meet.
    const float BannerHeight = 30f;
    const float ContentTop = 40f;
    const float OverlayWidth = 430f;
    const float OverlayHeight = 620f;
    const float MenuWidth = 700f;
    const float MenuHeight = 220f;
    const float DockLeft = 680f;
    const float DockGap = 8f;
    const float DockBottom = 900f;

    public Camera WorldCamera;
    public Transform[] ResidentRoots;
    public Transform[] StationRoots;

    public float TextScale = 1f;
    public bool Greyscale;
    public string GreyscaleStatus = "needs";
    public string FocusId = string.Empty;
    public string Notice = string.Empty;
    public string State = "Camera";
    public bool Paused;
    public bool MenuOpen;
    public bool MeasureMode;
    public string MeasureId = string.Empty;
    public float MeasureScale = 1f;
    public CursorKind CursorKind = CursorKind.Default;
    public bool HighContrastCursor;
    public double CursorPx = 32.0;
    public Vector2 Pointer = new Vector2(960f, 540f);

    public readonly List<ViewRow> HudRows = new List<ViewRow>();
    public readonly List<ViewRow> MenuRows = new List<ViewRow>();
    public string MenuLabel = string.Empty;
    public string MenuMessage = string.Empty;
    public string MenuFocusedId = string.Empty;

    static readonly string[] CursorStates = { "Default", "HoverInteractable", "HoverResident", "Blocked", "Busy", "Placement" };
    static readonly string[] GreyscaleStates = { "needs", "mood", "selection", "blocked" };
    static readonly string[] GreyscaleCues = { "[N] needs", "[M] mood", "[S] selection", "[B] blocked" };

    readonly List<ViewElement> layoutCache = new List<ViewElement>();
    GUIStyle label;
    GUIStyle small;
    GUIStyle title;

    public static Color32 ColorOf(string kind, bool greyscale)
    {
        if (greyscale) return Paper;
        if (kind == "menu") return new Color32(245, 245, 245, 255);
        if (kind == "overlay") return Paper;
        return Paper;
    }

    public IReadOnlyList<ViewElement> Layout()
    {
        float s = TextScale;
        Rect overlay = OverlayRect(s);
        Rect menu = MenuRect(s);
        layoutCache.Clear();
        Add("menu", "menu", menu, true, "Action menu");
        if (MenuOpen || MeasureMode)
            for (int i = 0; i < MenuRows.Count; i++)
                Add(MenuRows[i].Id, "menu-row",
                    new Rect(menu.x + 24f * s, menu.y + (80f + i * 70f) * s, 652f * s, 54f * s),
                    true, MenuRows[i].Label);
        Add("overlay", "overlay", overlay, true, "HUD overlay");
        Add("on-screen", "on-screen", new Rect(overlay.x + 24f, overlay.y + 72f * s, 200f * s, 52f * s), true, "Pause");
        float residentSize = 64f * s;
        float stationSize = 96f * s;
        float gap = DockGap * s;
        float stationTop = DockBottom - stationSize;
        float residentTop = stationTop - 12f * s - residentSize;
        for (int i = 0; i < 8; i++)
            Add("world-resident:" + i, "world-resident",
                new Rect(DockLeft + i * (residentSize + gap), residentTop, residentSize, residentSize),
                true, "Resident " + (i + 1));
        for (int i = 0; i < 8; i++)
            Add("world-object:" + i, "world-object",
                new Rect(DockLeft + i * (stationSize + gap), stationTop, stationSize, stationSize),
                true, "Station " + (i + 1));
        for (int i = 0; i < 8; i++)
            Add("hud:portrait:" + i, "hud-row",
                new Rect(overlay.x + 24f, overlay.y + (154f + i * 42f) * s, 280f * s, 36f * s),
                true, "Portrait");
        Add("camera-right", "button",
            new Rect(overlay.x + 24f, overlay.y + 496f * s, 190f * s, 52f * s), true, "Camera right");
        Add("persist-button", "button",
            new Rect(overlay.x + 24f, overlay.y + 556f * s, 250f * s, 52f * s), true, "Reapply persisted");
        Add("snap-target", "snap",
            new Rect(DockLeft + 8f * (residentSize + gap), residentTop, residentSize, residentSize),
            true, "Snap target");
        for (int i = 0; i < CursorStates.Length; i++)
            Add("cursor-target:" + CursorStates[i], "cursor-target",
                new Rect(700f + i * 190f, 930f, 170f, 72f), true, CursorStates[i]);
        return layoutCache;
    }

    Rect OverlayRect(float s)
    {
        return new Rect(24f, ContentTop * s, OverlayWidth * s, OverlayHeight * s);
    }

    Rect MenuRect(float s)
    {
        return new Rect(24f + OverlayWidth * s + 24f, ContentTop * s, MenuWidth * s, MenuHeight * s);
    }

    Rect BannerRect()
    {
        return new Rect(0f, 0f, Screen.width, BannerHeight * TextScale);
    }

    void Add(string id, string kind, Rect rect, bool interactive, string label)
    {
        rect = ClampToScreen(rect);
        ViewElement element = new ViewElement(id, kind, rect, interactive, label);
        if (Greyscale)
        {
            element.Background = Paper;
            element.Foreground = Ink;
        }
        else
        {
            element.Background = ColorOf(kind, false);
            element.Foreground = Ink;
        }
        layoutCache.Add(element);
    }

    static Rect ClampToScreen(Rect rect)
    {
        float x = Mathf.Clamp(rect.x, 4f, Mathf.Max(4f, Screen.width - rect.width - 4f));
        float y = Mathf.Clamp(rect.y, 4f, Mathf.Max(4f, Screen.height - rect.height - 4f));
        return new Rect(x, y, rect.width, rect.height);
    }

    public ViewElement Find(string id)
    {
        IReadOnlyList<ViewElement> elements = Layout();
        for (int i = 0; i < elements.Count; i++)
            if (elements[i].Id == id) return elements[i];
        return null;
    }

    public Rect RectFor(string id)
    {
        ViewElement element = Find(id);
        return element == null ? new Rect(0f, 0f, 0f, 0f) : element.Rect;
    }

    // Smallest actionable rectangle wins, so a row on the menu panel is not
    // shadowed by the panel itself or by a world token docked behind it.
    public string HitTest(Vector2 point)
    {
        IReadOnlyList<ViewElement> elements = Layout();
        ViewElement best = null;
        for (int i = 0; i < elements.Count; i++)
        {
            ViewElement element = elements[i];
            if (!element.Interactive || !element.Rect.Contains(point)) continue;
            if (best == null
                || element.Rect.width * element.Rect.height < best.Rect.width * best.Rect.height)
                best = element;
        }
        return best == null ? string.Empty : best.Id;
    }

    void OnGUI()
    {
        if (Event.current.type != EventType.Repaint) return;
        label = MakeStyle(Mathf.RoundToInt(16f * TextScale), Ink);
        small = MakeStyle(Mathf.RoundToInt(12f * TextScale), Ink);
        title = MakeStyle(Mathf.RoundToInt(20f * TextScale), Ink);
        if (Greyscale) DrawGreyscale();
        else DrawControls();
    }

    static GUIStyle MakeStyle(int size, Color color)
    {
        GUIStyle style = new GUIStyle(GUI.skin.label);
        style.fontSize = size;
        style.normal.textColor = color;
        style.wordWrap = true;
        style.clipping = TextClipping.Clip;
        return style;
    }

    void DrawControls()
    {
        DrawBanner();
        DrawPanel("overlay", "HUD overlay");
        DrawHudRows();
        DrawPauseButton();
        DrawPersistButton();
        DrawCameraButton();
        DrawWorldTokens();
        DrawSnapTarget();
        DrawCursorTargets();
        // Modal menu paints last so it always covers the world-marker layer.
        if (MenuOpen || MeasureMode) DrawMenu();
        DrawCursorGlyph();
        DrawFocusRing();
        if (MeasureMode) DrawMeasureBanner();
    }

    void DrawBanner()
    {
        Rect band = BannerRect();
        Fill(band, Paper);
        GUI.Label(new Rect(6f, 4f * TextScale, Screen.width - 12f, band.height - 8f * TextScale),
            Banner(), small);
    }

    string Banner()
    {
        return "CONTROLS-PLAY  state=" + State + "  scale=" + TextScale.ToString("0.0")
            + "  paused=" + (Paused ? "yes" : "no") + "  cursor=" + CursorKind
            + "  " + Notice;
    }

    void DrawPanel(string id, string heading)
    {
        ViewElement element = Find(id);
        if (element == null) return;
        Fill(element.Rect, element.Background);
        Stroke(element.Rect, Ink, 2f);
        GUI.Label(new Rect(element.Rect.x + 10f, element.Rect.y + 6f,
            element.Rect.width - 20f, 30f * TextScale), heading, title);
    }

    void DrawHudRows()
    {
        for (int i = 0; i < HudRows.Count && i < 8; i++)
        {
            ViewElement element = Find("hud:portrait:" + i);
            if (element == null) continue;
            Fill(element.Rect, RowPaper);
            Stroke(element.Rect, HudRows[i].Focused ? FocusInk : MutedInk, HudRows[i].Focused ? 3f : 1f);
            GUI.Label(new Rect(element.Rect.x + 6f, element.Rect.y + 2f,
                element.Rect.width - 12f, element.Rect.height - 4f), HudRows[i].Label, small);
        }
    }

    void DrawPauseButton()
    {
        ViewElement element = Find("on-screen");
        if (element == null) return;
        Fill(element.Rect, element.Background);
        Stroke(element.Rect, Ink, 2f);
        GUI.Label(new Rect(element.Rect.x + 8f, element.Rect.y + 4f,
            element.Rect.width - 16f, element.Rect.height - 8f),
            Paused ? "Resume (paused)" : "Pause", label);
    }

    void DrawPersistButton()
    {
        ViewElement element = Find("persist-button");
        if (element == null) return;
        Fill(element.Rect, RowPaper);
        Stroke(element.Rect, Ink, 2f);
        GUI.Label(new Rect(element.Rect.x + 8f, element.Rect.y + 4f,
            element.Rect.width - 16f, element.Rect.height - 8f), "Reapply persisted settings", small);
    }

    void DrawCameraButton()
    {
        ViewElement element = Find("camera-right");
        if (element == null) return;
        Fill(element.Rect, RowPaper);
        Stroke(element.Rect, Ink, 2f);
        GUI.Label(new Rect(element.Rect.x + 8f, element.Rect.y + 4f,
            element.Rect.width - 16f, element.Rect.height - 8f), "Camera pan right", small);
    }

    void DrawMenu()
    {
        ViewElement panel = Find("menu");
        if (panel == null) return;
        Fill(panel.Rect, panel.Background);
        Stroke(panel.Rect, Ink, 2f);
        GUI.Label(new Rect(panel.Rect.x + 14f, panel.Rect.y + 8f,
            panel.Rect.width - 28f, 30f * TextScale), "Action menu: " + MenuLabel, title);
        GUI.Label(new Rect(panel.Rect.x + 14f, panel.Rect.y + 42f * TextScale,
            panel.Rect.width - 28f, 24f * TextScale), MenuMessage, small);
        for (int i = 0; i < MenuRows.Count; i++)
        {
            ViewElement row = Find(MenuRows[i].Id);
            if (row == null) continue;
            Fill(row.Rect, MenuRows[i].Focused ? RowPaper : Paper);
            Stroke(row.Rect, MenuRows[i].Focused ? FocusInk : Ink, MenuRows[i].Focused ? 4f : 2f);
            GUI.Label(new Rect(row.Rect.x + 10f, row.Rect.y + 4f,
                row.Rect.width - 20f, row.Rect.height - 8f), MenuRows[i].Label, label);
        }
    }

    void DrawWorldTokens()
    {
        if (ResidentRoots != null)
        {
            for (int i = 0; i < ResidentRoots.Length; i++)
            {
                string id = "world-resident:" + i;
                ViewElement element = Find(id);
                if (element == null) continue;
                Fill(element.Rect, element.Background);
                Stroke(element.Rect, Ink, 2f);
                GUI.Label(new Rect(element.Rect.x + 4f, element.Rect.y + 2f,
                    element.Rect.width - 8f, element.Rect.height - 4f), (i + 1).ToString(), label);
            }
        }
        if (StationRoots != null)
        {
            for (int i = 0; i < StationRoots.Length; i++)
            {
                string id = "world-object:" + i;
                ViewElement element = Find(id);
                if (element == null) continue;
                Fill(element.Rect, RowPaper);
                Stroke(element.Rect, Ink, 2f);
                GUI.Label(new Rect(element.Rect.x + 4f, element.Rect.y + 2f,
                    element.Rect.width - 8f, element.Rect.height - 4f), "ST" + (i + 1), label);
            }
        }
    }

    void DrawSnapTarget()
    {
        ViewElement element = Find("snap-target");
        if (element == null) return;
        Stroke(element.Rect, FocusInk, 3f);
        GUI.Label(new Rect(element.Rect.x - 10f * TextScale,
            element.Rect.y - 24f * TextScale, 220f * TextScale, 20f * TextScale),
            "snap target", small);
    }

    void DrawCursorTargets()
    {
        for (int i = 0; i < CursorStates.Length; i++)
        {
            ViewElement element = Find("cursor-target:" + CursorStates[i]);
            if (element == null) continue;
            bool active = CursorStates[i] == CursorKind.ToString();
            Fill(element.Rect, active ? RowPaper : Paper);
            Stroke(element.Rect, active ? FocusInk : MutedInk, active ? 4f : 1f);
            GUI.Label(new Rect(element.Rect.x + 6f, element.Rect.y + 4f,
                element.Rect.width - 12f, element.Rect.height - 8f), element.Label, small);
        }
    }

    void DrawCursorGlyph()
    {
        float px = (float)CursorPx;
        Rect glyph = new Rect(Pointer.x - px * 0.5f, Pointer.y - px * 0.5f, px, px);
        // Opaque paper keeps every ink edge legible over arbitrary world content.
        Fill(new Rect(glyph.x - 2f, glyph.y - 2f, px + 4f, px + 4f), Paper);
        DrawCursorShape(glyph, HighContrastCursor ? Ink : MutedInk);
        // Tag sits in the reserved strip below the cursor-state row, so it can
        // never cover a HUD label, an action-menu row or a cursor-state button.
        float tagWidth = 240f * TextScale;
        Rect tag = new Rect(
            Mathf.Clamp(Pointer.x - tagWidth * 0.5f, 8f, Screen.width - tagWidth - 8f),
            1008f, tagWidth, 26f * TextScale);
        Fill(tag, Paper);
        Stroke(tag, Ink, 1f);
        GUIStyle tagStyle = MakeStyle(Mathf.RoundToInt(12f * TextScale), Ink);
        tagStyle.wordWrap = false;
        GUI.Label(new Rect(tag.x + 6f, tag.y + 3f * TextScale, tag.width - 12f,
            tag.height - 6f * TextScale),
            CursorKind + (HighContrastCursor ? " high-contrast" : " standard") + " " + px + "px",
            tagStyle);
    }

    void DrawCursorShape(Rect glyph, Color32 ink)
    {
        float px = glyph.width;
        int rows = Mathf.Max(8, Mathf.RoundToInt(px / 4f));
        switch (CursorKind)
        {
            case CursorKind.HoverInteractable:
                Fill(new Rect(glyph.x + px * 0.38f, glyph.y + px * 0.08f, px * 0.24f, px * 0.84f), ink);
                Fill(new Rect(glyph.x + px * 0.08f, glyph.y + px * 0.38f, px * 0.84f, px * 0.24f), ink);
                break;
            case CursorKind.HoverResident:
                for (int i = 0; i < rows; i++)
                {
                    float t = (i + 0.5f) / rows;
                    float width = px * (0.12f + 0.76f * (1f - Mathf.Abs(2f * t - 1f)));
                    Fill(new Rect(glyph.x + (px - width) * 0.5f, glyph.y + px * i / rows, width, px / rows), ink);
                }
                break;
            case CursorKind.Blocked:
                for (int i = 0; i < rows; i++)
                {
                    float t = (i + 0.5f) / rows;
                    float cell = px * 0.18f;
                    float x = (px - cell) * t;
                    Fill(new Rect(glyph.x + x, glyph.y + px * i / rows, cell, px / rows), ink);
                    Fill(new Rect(glyph.x + px - cell - x, glyph.y + px * i / rows, cell, px / rows), ink);
                }
                break;
            case CursorKind.Busy:
                for (int i = 0; i < rows; i++)
                {
                    float t = (i + 0.5f) / rows;
                    float width = px * (0.18f + 0.64f * Mathf.Abs(2f * t - 1f));
                    Fill(new Rect(glyph.x + (px - width) * 0.5f, glyph.y + px * i / rows, width, px / rows), ink);
                }
                break;
            case CursorKind.Placement:
                float arm = px * 0.28f, thick = px * 0.12f;
                Fill(new Rect(glyph.x, glyph.y, arm, thick), ink);
                Fill(new Rect(glyph.x, glyph.y, thick, arm), ink);
                Fill(new Rect(glyph.x + px - arm, glyph.y, arm, thick), ink);
                Fill(new Rect(glyph.x + px - thick, glyph.y, thick, arm), ink);
                Fill(new Rect(glyph.x, glyph.y + px - thick, arm, thick), ink);
                Fill(new Rect(glyph.x, glyph.y + px - arm, thick, arm), ink);
                Fill(new Rect(glyph.x + px - arm, glyph.y + px - thick, arm, thick), ink);
                Fill(new Rect(glyph.x + px - thick, glyph.y + px - arm, thick, arm), ink);
                Fill(new Rect(glyph.x + px * 0.42f, glyph.y + px * 0.42f, px * 0.16f, px * 0.16f), ink);
                break;
            default:
                int head = Mathf.Max(4, rows * 3 / 5);
                for (int i = 0; i < head; i++)
                {
                    float width = px * (0.10f + 0.72f * (i + 0.5f) / head);
                    Fill(new Rect(glyph.x, glyph.y + px * i / rows, width, px / rows), ink);
                }
                Fill(new Rect(glyph.x + px * 0.20f, glyph.y + px * head / rows,
                    px * 0.16f, px * (rows - head) / rows), ink);
                break;
        }
    }

    void DrawFocusRing()
    {
        if (string.IsNullOrEmpty(FocusId)) return;
        ViewElement element = Find(FocusId);
        if (element == null) return;
        Stroke(element.Rect, FocusInk, 3f);
    }

    void DrawMeasureBanner()
    {
        Rect band = new Rect(0f, Screen.height - 28f, Screen.width, 28f);
        Fill(band, Paper);
        GUI.Label(new Rect(8f, Screen.height - 26f, Screen.width - 16f, 24f),
            "MEASURED " + MeasureId + " @ text scale " + MeasureScale.ToString("0.0")
            + "  frame 1920x1080", small);
    }

    void DrawGreyscale()
    {
        Rect band = BannerRect();
        Fill(band, Paper);
        GUI.Label(new Rect(6f, 4f * TextScale, Screen.width - 12f, band.height - 8f * TextScale),
            "CONTROLS-PLAY greyscale status=" + GreyscaleStatus + "  scale=" + TextScale.ToString("0.0")
            + "  no colour-only cue", small);
        Rect panel = new Rect(560f, 160f, 800f, 520f);
        Fill(panel, Paper);
        Stroke(panel, Ink, 3f);
        GUI.Label(new Rect(panel.x + 20f, panel.y + 16f, panel.width - 40f, 40f),
            "STATUS: " + GreyscaleStatus.ToUpperInvariant(), title);
        for (int i = 0; i < GreyscaleStates.Length; i++)
        {
            bool active = GreyscaleStates[i] == GreyscaleStatus;
            Rect chip = new Rect(panel.x + 24f + i * 188f, panel.y + 120f, 170f, 200f);
            Fill(chip, active ? Ink : RowPaper);
            Stroke(chip, Ink, 2f);
            GUIStyle style = MakeStyle(Mathf.RoundToInt(18f * TextScale), active ? Paper : Ink);
            GUI.Label(new Rect(chip.x + 10f, chip.y + 12f, chip.width - 20f, chip.height - 24f),
                GreyscaleCues[i], style);
        }
        GUI.Label(new Rect(panel.x + 24f, panel.y + 360f, panel.width - 48f, 120f),
            "Needs, mood, selection and blocked carry shape and text cues; "
            + "the frame is rendered with a grey palette.", label);
    }

    void Fill(Rect rect, Color32 color)
    {
        Color previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = previous;
    }

    void Stroke(Rect rect, Color32 color, float width)
    {
        Fill(new Rect(rect.x, rect.y, rect.width, width), color);
        Fill(new Rect(rect.x, rect.yMax - width, rect.width, width), color);
        Fill(new Rect(rect.x, rect.y, width, rect.height), color);
        Fill(new Rect(rect.xMax - width, rect.y, width, rect.height), color);
    }
}
