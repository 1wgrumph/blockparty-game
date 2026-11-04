using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Blockparty.UI;
using UnityEngine;

// CONTROLS-PLAY@1: records the render-readback measurements for the receipt.
// Contrast is WCAG 2.x relative-luminance ratio over the exact foreground and
// background colours ControlsView paints; rectangles come from the same
// ControlsView layout at 1920x1080. This is recorded render readback, not an
// independent pixel re-measurement (named gap G-6).
public sealed class MeasureProbe
{
    public sealed class ElementSpec
    {
        public readonly string Id;
        public readonly string Type;

        public ElementSpec(string id, string type)
        {
            Id = id;
            Type = type;
        }
    }

    public sealed class Sample
    {
        public string Element;
        public double TextScale;
        public float Width;
        public float Height;
        public double Contrast;
        public double FocusContrast;
        public string FramePath;
    }

    public sealed class GreySample
    {
        public string Status;
        public string FramePath;
    }

    public sealed class CursorSample
    {
        public string State;
        public double Size;
        public bool HighContrast;
        public int Px;
        public double Contrast;
        public string FramePath;
    }

    public static readonly ElementSpec[] Elements =
    {
        new ElementSpec("menu", "menu"),
        new ElementSpec("overlay", "overlay"),
        new ElementSpec("on-screen", "on-screen"),
        new ElementSpec("world-resident:0", "world-resident"),
        new ElementSpec("world-object:0", "world-object"),
    };

    public static readonly double[] Scales = { 1.0, 1.5 };
    public static readonly double[] CursorSizes =
    {
        Blockparty.UI.Accessibility.CursorSizeMin, 1.0, Blockparty.UI.Accessibility.CursorSizeMax
    };
    public static readonly string[] GreyscaleStates = { "needs", "mood", "selection", "blocked" };

    public readonly List<Sample> Samples = new List<Sample>();
    public readonly List<GreySample> Greys = new List<GreySample>();
    public readonly List<CursorSample> Cursors = new List<CursorSample>();

    public static double Luminance(Color32 color)
    {
        double r = Channel(color.r), g = Channel(color.g), b = Channel(color.b);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    static double Channel(byte value)
    {
        double s = value / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    public static double Contrast(Color32 foreground, Color32 background)
    {
        double a = Luminance(foreground), b = Luminance(background);
        double lighter = Math.Max(a, b), darker = Math.Min(a, b);
        return (lighter + 0.05) / (darker + 0.05);
    }

    public static int PixelsFor(double size)
    {
        return (int)Math.Round(32.0 * size, MidpointRounding.AwayFromZero);
    }

    // Fails the evidence run rather than recording an impossible threshold.
    public static void SelfCheck()
    {
        double blackWhite = Contrast(ControlsView.Ink, ControlsView.Paper);
        double focus = Contrast(ControlsView.FocusInk, ControlsView.Paper);
        int a = PixelsFor(Accessibility.CursorSizeMin);
        int b = PixelsFor(1.0);
        int c = PixelsFor(Accessibility.CursorSizeMax);
        if (Math.Abs(blackWhite - 21.0) > 0.01)
            throw new InvalidOperationException("contrast self-check black/white=" + blackWhite);
        if (focus < 4.5)
            throw new InvalidOperationException("contrast self-check focus=" + focus);
        if (a == b || b == c || a == c)
            throw new InvalidOperationException("cursor px self-check " + a + "/" + b + "/" + c);
    }

    public Sample AddSample(string element, double scale, Rect rect, Color32 fg, Color32 bg,
        Color32 focusInk, Color32 focusBackground, string framePath)
    {
        Sample sample = new Sample
        {
            Element = element,
            TextScale = scale,
            Width = rect.width,
            Height = rect.height,
            Contrast = Contrast(fg, bg),
            FocusContrast = Contrast(focusInk, focusBackground),
            FramePath = framePath,
        };
        Samples.Add(sample);
        return sample;
    }

    public void AddGrey(string status, string framePath)
    {
        Greys.Add(new GreySample { Status = status, FramePath = framePath });
    }

    static double CursorContrast(string framePath, int px, bool highContrast)
    {
        Texture2D frame = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try
        {
            if (!frame.LoadImage(System.IO.File.ReadAllBytes(framePath)))
                throw new InvalidOperationException("cursor frame cannot be decoded " + framePath);
            Color32[] pixels = frame.GetPixels32();
            Color32 ink = highContrast ? ControlsView.Ink : ControlsView.MutedInk;
            // CaptureMeasurements parks the pointer at (1150,620), in GUI coordinates.
            int left = 1150 - px / 2, top = 620 - px / 2;
            int[] neighbors = { -1, 1, -frame.width, frame.width };
            double minimum = double.PositiveInfinity;
            for (int y = top; y < top + px; y++)
            for (int x = left; x < left + px; x++)
            {
                int index = (frame.height - 1 - y) * frame.width + x;
                Color32 foreground = pixels[index];
                if (!foreground.Equals(ink)) continue;
                foreach (int offset in neighbors)
                {
                    Color32 backdrop = pixels[index + offset];
                    if (!backdrop.Equals(ink)) minimum = Math.Min(minimum, Contrast(foreground, backdrop));
                }
            }
            if (double.IsPositiveInfinity(minimum))
                throw new InvalidOperationException("cursor frame has no ink/backdrop edges " + framePath);
            return minimum;
        }
        finally { UnityEngine.Object.Destroy(frame); }
    }

    public CursorSample AddCursor(string state, double size, bool highContrast, string framePath)
    {
        CursorSample sample = new CursorSample
        {
            State = state,
            Size = size,
            HighContrast = highContrast,
            Px = PixelsFor(size),
            Contrast = CursorContrast(framePath, PixelsFor(size), highContrast),
            FramePath = framePath,
        };
        Cursors.Add(sample);
        return sample;
    }

    public static string Sha256File(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (System.IO.FileStream stream = System.IO.File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    public static string Ref(string path)
    {
        return "{\"path\":" + Quote(path) + ",\"sha256\":\"" + Sha256File(path) + "\"}";
    }

    public static string Quote(string value)
    {
        if (value == null) return "\"\"";
        StringBuilder sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    public static string Number(double value)
    {
        return value.ToString("0.0###", CultureInfo.InvariantCulture);
    }

    public string BuildJson(string tree, string binary, Func<string, string> frameRef)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"tree_hash\":\"").Append(tree).Append("\",\"binary_sha256\":\"").Append(binary).Append('"');
        sb.Append(",\"width\":1920,\"height\":1080,\"elements\":[");
        for (int i = 0; i < Elements.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(Quote(Elements[i].Id))
              .Append(",\"type\":").Append(Quote(Elements[i].Type))
              .Append(",\"interactive\":true}");
        }
        sb.Append("],\"samples\":[");
        for (int i = 0; i < Samples.Count; i++)
        {
            Sample s = Samples[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"element\":").Append(Quote(s.Element))
              .Append(",\"text_scale\":").Append(Number(s.TextScale))
              .Append(",\"width\":").Append(Number(s.Width))
              .Append(",\"height\":").Append(Number(s.Height))
              .Append(",\"contrast\":").Append(Number(s.Contrast))
              .Append(",\"focus_contrast\":").Append(Number(s.FocusContrast))
              .Append(",\"frame\":").Append(frameRef(s.FramePath)).Append('}');
        }
        sb.Append("],\"greyscale\":[");
        for (int i = 0; i < Greys.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"status\":").Append(Quote(Greys[i].Status))
              .Append(",\"frame\":").Append(frameRef(Greys[i].FramePath)).Append('}');
        }
        sb.Append("],\"cursors\":[");
        for (int i = 0; i < Cursors.Count; i++)
        {
            CursorSample c = Cursors[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"state\":").Append(Quote(c.State))
              .Append(",\"size\":").Append(Number(c.Size))
              .Append(",\"high_contrast\":").Append(c.HighContrast ? "true" : "false")
              .Append(",\"px\":").Append(c.Px.ToString(CultureInfo.InvariantCulture))
              .Append(",\"contrast\":").Append(Number(c.Contrast))
              .Append(",\"frame\":").Append(frameRef(c.FramePath)).Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }
}
