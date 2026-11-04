// CRAFTS@1 procedural fallback art: a deterministic, self-made image per craft
// kind, painted only with the approved mockup palette (POLISH-HUD style-guide
// colours) plus the resident's skin and hair swatches. Plain C# 9, no engine
// dependency, no anti-aliasing, so every pixel is an exact palette colour.
// Output is RGBA32 with row 0 at the bottom (Texture2D.SetPixels32 order).
using System;

namespace Blockparty.Activities
{
    public static class CraftArt
    {
        public const uint Ivory = 0xF4EBDD, Ink = 0x243238, Teal = 0x3F777B, TealDark = 0x2C5A5D,
            Rust = 0xAD5737, Denim = 0x405A70, Gold = 0xF2B24C, Olive = 0x667451, Coral = 0xD0603F,
            Inset = 0xE2D6C3, Sky = 0xBFD8D6, Clay = 0xC79A72;

        // Same swatches as AlphaHud's Skin and HairTones.
        public static readonly uint[] Skin = { 0xF6EDE4, 0xF7EAD0, 0xD6BD95, 0xA07E56, 0x604034, 0x292420 };
        public static readonly uint[] Hair = { 0x1F1A17, 0x5A3A26, 0x8D5A3B, 0xC9883F, 0xE0C27A, 0x8A8580 };
        public static readonly uint[] Palette =
            { Ivory, Ink, Teal, TealDark, Rust, Denim, Gold, Olive, Coral, Inset, Sky, Clay };

        // Pixel size of the image for a kind (wall art follows its frame shape).
        public static void SizeOf(CraftKind kind, out int width, out int height)
        {
            switch (kind)
            {
                case CraftKind.Portrait: width = 96; height = 128; return;
                case CraftKind.Poster: width = 96; height = 128; return;
                case CraftKind.StainedGlass: width = 96; height = 128; return;
                case CraftKind.Painting: width = 128; height = 96; return;
                case CraftKind.Quilt: width = 144; height = 96; return;
                default: width = 128; height = 64; return;
            }
        }

        public static byte[] Render(CraftKind kind, int seed, int skinTone, int hairColor)
        {
            int w, h;
            SizeOf(kind, out w, out h);
            uint[] px = new uint[w * h];
            Rng rng = new Rng(seed);
            switch (kind)
            {
                case CraftKind.Portrait: Portrait(px, w, h, rng, Skin[Clamp(skinTone, Skin.Length)], Hair[Clamp(hairColor, Hair.Length)]); break;
                case CraftKind.Painting: Painting(px, w, h, rng); break;
                case CraftKind.Poster: Poster(px, w, h, rng); break;
                case CraftKind.Quilt: Quilt(px, w, h, rng); break;
                case CraftKind.StainedGlass: Glass(px, w, h, rng); break;
                default: Glaze(px, w, h, rng); break;
            }
            byte[] rgba = new byte[w * h * 4];
            for (int i = 0; i < px.Length; i++)
            {
                rgba[i * 4] = (byte)(px[i] >> 16);
                rgba[i * 4 + 1] = (byte)(px[i] >> 8);
                rgba[i * 4 + 2] = (byte)px[i];
                rgba[i * 4 + 3] = 255;
            }
            return rgba;
        }

        // ---------- kinds (coordinates: x right, y up, 0..w-1 / 0..h-1) ----------

        static void Portrait(uint[] px, int w, int h, Rng rng, uint skin, uint hair)
        {
            uint back = rng.Pick(Teal, Denim, Olive);
            uint shirt = rng.Pick(Rust, Gold, Coral);
            Fill(px, back);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    // Lighter halo behind the head.
                    if (Ellipse(x, y, w * 0.5f, h * 0.6f, w * 0.42f, h * 0.36f)) px[y * w + x] = back == Teal ? Sky : Inset;
                }
            float cx = w * 0.5f, headY = h * 0.6f, rx = w * 0.2f, ry = h * 0.18f;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (Ellipse(x, y, cx, h * 0.02f, w * 0.46f, h * 0.3f)) px[i] = shirt;
                    else if (Math.Abs(x - cx) < w * 0.07f && y < headY - ry * 0.6f && y > h * 0.2f) px[i] = skin;
                    if (Ellipse(x, y, cx, headY + ry * 0.35f, rx * 1.18f, ry * 0.95f) && y > headY) px[i] = hair;
                    if (Ellipse(x, y, cx, headY, rx, ry)) px[i] = skin;
                    if (Ellipse(x, y, cx, headY + ry * 0.75f, rx * 1.05f, ry * 0.42f) && y > headY + ry * 0.45f) px[i] = hair;
                }
            int eyeY = (int)(headY + ry * 0.1f);
            Dot(px, w, h, (int)(cx - rx * 0.42f), eyeY, 2, Ink);
            Dot(px, w, h, (int)(cx + rx * 0.42f), eyeY, 2, Ink);
            Dot(px, w, h, (int)(cx - rx * 0.62f), eyeY - 6, 2, Coral);
            Dot(px, w, h, (int)(cx + rx * 0.62f), eyeY - 6, 2, Coral);
            for (int x = (int)(cx - rx * 0.35f); x <= (int)(cx + rx * 0.35f); x++)
            {
                float t = (x - cx) / (rx * 0.35f);
                int y = (int)(headY - ry * 0.45f + t * t * 3f);
                Set(px, w, h, x, y, Ink);
            }
        }

        static void Painting(uint[] px, int w, int h, Rng rng)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    px[y * w + x] = y > h * 0.78f ? Sky : y > h * 0.5f ? Inset : Ivory;
            float sunX = w * (0.65f + rng.Next(20) / 100f);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (Ellipse(x, y, sunX, h * 0.78f, 9, 9)) px[y * w + x] = Gold;
            uint[] hills = { Teal, Olive, TealDark };
            for (int layer = 0; layer < 3; layer++)
            {
                float baseY = h * (0.55f - layer * 0.16f), amp = 6f + layer * 2f, freq = 0.05f + rng.Next(30) / 1000f;
                float phase = rng.Next(628) / 100f;
                for (int x = 0; x < w; x++)
                {
                    int top = (int)(baseY + Math.Sin(x * freq + phase) * amp);
                    for (int y = 0; y <= top && y < h; y++) px[y * w + x] = hills[layer];
                }
            }
            for (int k = 0; k < 3; k++)
            {
                int hx = 14 + k * 38 + rng.Next(10), hy = (int)(h * 0.12f) + rng.Next(6);
                Rect(px, w, h, hx, hy, 14, 11, k % 2 == 0 ? Ivory : Gold);
                Rect(px, w, h, hx + 5, hy, 4, 6, Denim);
                for (int r = 0; r < 8; r++) Rect(px, w, h, hx - 1 + r, hy + 11 + r, 16 - r * 2, 1, Rust);
            }
        }

        static void Poster(uint[] px, int w, int h, Rng rng)
        {
            uint back = rng.Pick(Gold, Coral, Teal);
            Fill(px, back);
            uint stripe = back == Teal ? Gold : Rust;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (((x + y) / 10) % 2 == 0 && y < h * 0.35f) px[i] = stripe;
                    if (Ellipse(x, y, w * 0.5f, h * 0.62f, w * 0.32f, w * 0.32f)) px[i] = Ivory;
                    if (Ellipse(x, y, w * 0.5f, h * 0.62f, w * 0.2f, w * 0.2f)) px[i] = back == Coral ? Denim : Coral;
                    if (y > h * 0.86f) px[i] = Ink;
                    if (y < h * 0.08f) px[i] = Ink;
                }
            for (int k = 0; k < 5; k++) Rect(px, w, h, 10 + k * 17, (int)(h * 0.9f), 9, 5, Ivory);
            Border(px, w, h, 3, Ivory);
        }

        static void Quilt(uint[] px, int w, int h, Rng rng)
        {
            uint[] patches = { Teal, Rust, Gold, Denim, Ivory, Olive, Coral };
            int cols = 6, rows = 4, pw = (w - 8) / cols, ph = (h - 8) / rows;
            Fill(px, Rust);
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    uint a = patches[rng.Next(patches.Length)], b = patches[rng.Next(patches.Length)];
                    bool diagonal = rng.Next(3) != 0;
                    int ox = 4 + c * pw, oy = 4 + r * ph;
                    for (int y = 0; y < ph; y++)
                        for (int x = 0; x < pw; x++)
                        {
                            bool seam = x == 0 || y == 0;
                            uint color = diagonal && x * ph > y * pw ? b : a;
                            Set(px, w, h, ox + x, oy + y, seam ? Ivory : color);
                        }
                }
            Border(px, w, h, 2, Ink);
        }

        static void Glass(uint[] px, int w, int h, Rng rng)
        {
            uint[] jewels = { Teal, Rust, Gold, Denim, Coral, Olive, Sky };
            int n = 13;
            float[] sx = new float[n], sy = new float[n];
            uint[] sc = new uint[n];
            for (int i = 0; i < n; i++)
            {
                sx[i] = 6 + rng.Next(w - 12);
                sy[i] = 6 + rng.Next(h - 12);
                sc[i] = jewels[rng.Next(jewels.Length)];
            }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float best = float.MaxValue, next = float.MaxValue;
                    int pick = 0;
                    for (int i = 0; i < n; i++)
                    {
                        float d = (x - sx[i]) * (x - sx[i]) + (y - sy[i]) * (y - sy[i]);
                        if (d < best) { next = best; best = d; pick = i; }
                        else if (d < next) next = d;
                    }
                    px[y * w + x] = Math.Sqrt(next) - Math.Sqrt(best) < 1.6 ? Ink : sc[pick];
                }
            Border(px, w, h, 4, Ink);
        }

        static void Glaze(uint[] px, int w, int h, Rng rng)
        {
            uint body = rng.Pick(Teal, Denim, Rust);
            uint accent = body == Rust ? Teal : Gold;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    uint c = body;
                    if (y < h * 0.12f) c = Clay;
                    else if (Math.Abs(y - h * 0.7f) < 3f || Math.Abs(y - h * 0.3f) < 2f) c = accent;
                    else
                    {
                        double wave = h * 0.5f + Math.Sin(x * Math.PI * 2.0 * 4.0 / w) * h * 0.08f;
                        if (Math.Abs(y - wave) < 2.0) c = Ivory;
                        if (y > h * 0.8f && (x % 16) < 3 && (y % 8) < 3) c = Ivory;
                    }
                    px[y * w + x] = c;
                }
        }

        // ---------- helpers ----------

        sealed class Rng
        {
            uint state;
            public Rng(int seed) { state = (uint)seed * 2654435761u ^ 0x9E3779B9u; if (state == 0) state = 1; }
            public int Next(int max)
            {
                state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                return (int)(state % (uint)Math.Max(1, max));
            }
            public uint Pick(uint a, uint b, uint c) { int k = Next(3); return k == 0 ? a : k == 1 ? b : c; }
        }

        static bool Ellipse(int x, int y, float cx, float cy, float rx, float ry)
        {
            float dx = (x - cx) / rx, dy = (y - cy) / ry;
            return dx * dx + dy * dy <= 1f;
        }

        static void Fill(uint[] px, uint c) { for (int i = 0; i < px.Length; i++) px[i] = c; }

        static void Set(uint[] px, int w, int h, int x, int y, uint c)
        {
            if (x >= 0 && y >= 0 && x < w && y < h) px[y * w + x] = c;
        }

        static void Rect(uint[] px, int w, int h, int x0, int y0, int rw, int rh, uint c)
        {
            for (int y = y0; y < y0 + rh; y++) for (int x = x0; x < x0 + rw; x++) Set(px, w, h, x, y, c);
        }

        static void Dot(uint[] px, int w, int h, int cx, int cy, int r, uint c)
        {
            for (int y = cy - r; y <= cy + r; y++)
                for (int x = cx - r; x <= cx + r; x++)
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r) Set(px, w, h, x, y, c);
        }

        static void Border(uint[] px, int w, int h, int t, uint c)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (x < t || y < t || x >= w - t || y >= h - t) px[y * w + x] = c;
        }

        static int Clamp(int value, int length) { return value < 0 ? 0 : value >= length ? length - 1 : value; }
    }
}
