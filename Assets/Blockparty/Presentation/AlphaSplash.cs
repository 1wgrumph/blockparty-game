using UnityEngine;
using UnityEngine.Rendering;

// CONTROLS-LOGO (M5-OD-37): the short splash / loading card shown when the
// Alpha player starts, right after the engine's own splash. Cream card, the self-made logo, the neighbourhood
// skyline strip and a loading bar. It stays up while the world builds and for
// at least HoldSeconds from the first frame (and MinAfterReady once the world
// exists), then fades (cut under reduced motion). Once the world is ready, any
// key or click skips it; the event is consumed so it never reaches the title.
// Art: UI/Art/Resources/BlockPartyLogo.png and BlockPartySkyline.png from
// evidence/production/CONTROLS-LOGO/tools/make-logo.py.
public sealed class AlphaSplash
{
    const float HoldSeconds = 2.5f, MinAfterReady = 1.2f, FadeSeconds = 0.35f;
    static readonly Color Cream = new Color(0xF4 / 255f, 0xEB / 255f, 0xDD / 255f),
        Ink = new Color(0x24 / 255f, 0x32 / 255f, 0x38 / 255f), Teal = new Color(0x3F / 255f, 0x77 / 255f, 0x7B / 255f),
        Track = new Color(0xE2 / 255f, 0xD6 / 255f, 0xC3 / 255f);

    Texture2D logo, skyline;
    GUIStyle style;
    float start = -1f, readyAt = -1f, fadeAt = -1f;

    public bool Done { get; private set; }
    public bool Holding { get { return !Done && fadeAt < 0f; } }
    public int Painted { get; private set; }
    // Presentation lags a few frames; the world build waits until the card has really reached the screen.
    public bool OnScreen { get { return Painted >= 6 && Time.realtimeSinceStartup - start >= 0.6f; } }

    // Draws the card; ready = the live world exists. Returns true while the card still covers the title.
    public bool Draw(bool ready, bool reducedMotion, float textScale)
    {
        if (Done) return false;
        if (!SplashScreen.isFinished) return true;   // the engine's own splash is still on screen; start after it
        float now = Time.realtimeSinceStartup;
        if (start < 0f)
        {
            start = now;
            logo = Resources.Load<Texture2D>("BlockPartyLogo");
            skyline = Resources.Load<Texture2D>("BlockPartySkyline");
            style = new GUIStyle { font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"), alignment = TextAnchor.MiddleCenter };
            Debug.Log("ALPHA splash shown w=" + Screen.width + " h=" + Screen.height);
        }
        if (ready && readyAt < 0f) { readyAt = now; Debug.Log("ALPHA splash ready after=" + (now - start).ToString("F2")); }
        Event e = Event.current;
        if (ready && fadeAt < 0f && (e.type == EventType.MouseDown || e.type == EventType.KeyDown))
        {
            e.Use();
            return Finish(now, "skipped");
        }
        if (ready && fadeAt < 0f && now - start >= HoldSeconds && now - readyAt >= MinAfterReady)
            fadeAt = reducedMotion ? now - FadeSeconds : now;
        float alpha = fadeAt < 0f ? 1f : 1f - (now - fadeAt) / FadeSeconds;
        if (alpha <= 0f) return Finish(now, "timer");
        if (e.type == EventType.Repaint) { Paint(alpha, ready, textScale, now); Painted++; }
        return fadeAt < 0f;
    }

    bool Finish(float now, string how)
    {
        Done = true;
        Debug.Log("ALPHA splash done how=" + how + " shown=" + (now - start).ToString("F2"));
        return false;
    }

    void Paint(float alpha, bool ready, float textScale, float now)
    {
        float W = Screen.width, H = Screen.height;
        Color old = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, alpha);
        GUI.DrawTexture(new Rect(0, 0, W, H), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, Faded(Cream, alpha), 0, 0);
        if (skyline != null)
        {
            float sh = W * skyline.height / skyline.width;
            GUI.DrawTexture(new Rect(0, H - sh, W, sh), skyline, ScaleMode.StretchToFill, true);
        }
        float lw = W * 0.52f, lh = lw * 448f / 1024f;
        if (logo != null)
        {
            lw = Mathf.Min(W * 0.52f, H * 0.42f * logo.width / logo.height);
            lh = lw * logo.height / logo.width;
            GUI.DrawTexture(new Rect((W - lw) / 2f, H * 0.36f - lh / 2f, lw, lh), logo, ScaleMode.ScaleToFit, true);
        }
        float u = Mathf.Clamp(H / 1080f, 0.6f, 2f);
        float y = H * 0.36f + lh / 2f + 32f * u;
        float bw = Mathf.Min(W * 0.3f, 420f * u), bh = 10f * u;
        float progress = Mathf.Clamp01((now - start) / HoldSeconds);
        if (ready) progress = Mathf.Max(progress, 0.6f);
        Rect bar = new Rect((W - bw) / 2f, y, bw, bh);
        GUI.DrawTexture(bar, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, Faded(Track, alpha), 0, bh / 2f);
        GUI.DrawTexture(new Rect(bar.x, bar.y, Mathf.Max(bh, bw * progress), bh), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, Faded(Teal, alpha), 0, bh / 2f);
        Label(new Rect(0, y + bh + 6f * u, W, 30f * u * textScale), ready ? "Ready" : "Loading the neighborhood…", 18f * u * textScale, Ink, false);
        GUI.color = old;
    }

    static Color Faded(Color c, float a) { c.a *= a; return c; }

    void Label(Rect r, string text, float size, Color color, bool bold)
    {
        style.fontSize = Mathf.Max(8, Mathf.RoundToInt(size));
        style.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        style.normal.textColor = color;
        GUI.Label(r, text, style);
    }
}
