using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Immediate-mode drawing in Minecraft's GUI units: everything is laid out in "GUI pixels" and multiplied by
    /// the GUI scale, exactly like Minecraft, so the hotbar and inventory come out the same size and crispness.
    /// </summary>
    public static class McGui
    {
        public static int Scale = 3;
        public static int ConfigScale; // 0 = auto, like Minecraft's "Auto" setting

        public static int ScaledWidth => Screen.width / Scale;
        public static int ScaledHeight => Screen.height / Scale;

        /// <summary>Minecraft's own auto scale: the largest factor that still leaves 320x240 GUI pixels.</summary>
        public static void UpdateScale()
        {
            int i = 1;
            int w = Screen.width, h = Screen.height;
            while (i != ConfigScale && i < w && i < h && w / (i + 1) >= 320 && h / (i + 1) >= 240) i++;
            Scale = i;
        }

        /// <summary>Draws a region of a texture given in the texture's own pixels (top-left origin, like Minecraft's blit).</summary>
        public static void Blit(Texture2D tex, float x, float y, float u, float v, float w, float h, float texW = 256, float texH = 256)
        {
            BlitSized(tex, x, y, w, h, u, v, w, h, texW, texH);
        }

        public static void BlitSized(Texture2D tex, float x, float y, float dw, float dh, float u, float v, float w, float h, float texW, float texH)
        {
            if (tex == null) return;
            var r = new Rect(x * Scale, y * Scale, dw * Scale, dh * Scale);
            var uv = new Rect(u / texW, 1f - (v + h) / texH, w / texW, h / texH);
            GUI.DrawTextureWithTexCoords(r, tex, uv, true);
        }

        public static void Fill(float x, float y, float w, float h, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(new Rect(x * Scale, y * Scale, w * Scale, h * Scale), Texture2D.whiteTexture);
            GUI.color = old;
        }

        /// <summary>Draws a Slime Rancher sprite (it may live in an atlas) into a GUI-pixel box.</summary>
        public static void Sprite(Sprite s, float x, float y, float w, float h)
        {
            if (s == null || s.texture == null) return;
            var tr = s.textureRect;
            var t = s.texture;
            var uv = new Rect(tr.x / t.width, tr.y / t.height, tr.width / t.width, tr.height / t.height);
            GUI.DrawTextureWithTexCoords(new Rect(x * Scale, y * Scale, w * Scale, h * Scale), t, uv, true);
        }
    }

    /// <summary>
    /// Minecraft's bitmap font (ascii.png, accented.png, nonlatin_european.png) with the same glyph widths and
    /// drop shadow, so French names with accents come out exactly like in Minecraft.
    /// </summary>
    public static class McFont
    {
        private struct Glyph
        {
            public Texture2D Tex;
            public float U, V, CellW, CellH;
            public float Height, Ascent, Advance;
        }

        private static readonly Dictionary<char, Glyph> glyphs = new Dictionary<char, Glyph>();
        private static bool loaded;

        private static void Load()
        {
            loaded = true;
            var p = Path.Combine(McAssets.Root, "font.txt");
            if (!File.Exists(p)) return;
            string file = null; float height = 8, ascent = 7;
            var rows = new List<string>();
            void Flush()
            {
                if (file == null || rows.Count == 0) { rows.Clear(); return; }
                var tex = McAssets.Tex("font/" + file);
                var px = tex.GetPixels32();
                int cols = 0;
                foreach (var r in rows) cols = Mathf.Max(cols, r.Length);
                int cw = tex.width / cols, ch = tex.height / rows.Count;
                for (int ry = 0; ry < rows.Count; ry++)
                    for (int cx = 0; cx < rows[ry].Length; cx++)
                    {
                        char c = rows[ry][cx];
                        if (c == '\0' || glyphs.ContainsKey(c)) continue;
                        // Rightmost column with any opaque pixel, as Minecraft's BitmapFont does.
                        int right = 0;
                        for (int x = cw - 1; x >= 0 && right == 0; x--)
                            for (int y = 0; y < ch; y++)
                            {
                                int ix = cx * cw + x, iy = tex.height - 1 - (ry * ch + y);
                                if (px[iy * tex.width + ix].a > 0) { right = x + 1; break; }
                            }
                        float f = height / ch;
                        glyphs[c] = new Glyph
                        {
                            Tex = tex, U = cx * cw, V = ry * ch, CellW = cw, CellH = ch,
                            Height = height, Ascent = ascent, Advance = (int)(0.5 + right * f) + 1,
                        };
                    }
                rows.Clear();
            }
            foreach (var line in File.ReadAllLines(p, System.Text.Encoding.UTF8))
            {
                if (line.StartsWith("P|"))
                {
                    Flush();
                    var parts = line.Split('|');
                    file = parts[1];
                    height = float.Parse(parts[2]);
                    ascent = float.Parse(parts[3]);
                }
                else if (line.StartsWith("R|")) rows.Add(line.Substring(2));
            }
            Flush();
        }

        /// <summary>
        /// Minecraft draws Chinese, Japanese and Korean with its unifont, which SlimeCraft does not carry; text with
        /// characters missing from the bitmap fonts falls back to the system font at the same size.
        /// </summary>
        private static bool NeedsFallback(string s)
        {
            foreach (var c in s) if (c != ' ' && !glyphs.ContainsKey(c)) return true;
            return false;
        }

        private static GUIStyle fallback;

        private static GUIStyle Fallback()
        {
            if (fallback == null) fallback = new GUIStyle(GUI.skin.label) { padding = new RectOffset(), margin = new RectOffset(), clipping = TextClipping.Overflow, wordWrap = false };
            fallback.fontSize = Mathf.RoundToInt(8.5f * McGui.Scale);
            return fallback;
        }

        public static float Width(string s)
        {
            if (!loaded) Load();
            if (NeedsFallback(s)) return Fallback().CalcSize(new GUIContent(s)).x / McGui.Scale;
            float w = 0;
            foreach (var c in s) w += Adv(c);
            return w;
        }

        private static float Adv(char c)
        {
            if (c == ' ') return 4;
            return glyphs.TryGetValue(c, out var g) ? g.Advance : 6;
        }

        public static void Draw(string s, float x, float y, Color color, bool shadow = true)
        {
            if (!loaded) Load();
            if (shadow) DrawRaw(s, x + 1, y + 1, new Color(color.r * 0.25f, color.g * 0.25f, color.b * 0.25f, color.a));
            DrawRaw(s, x, y, color);
        }

        public static void DrawCentered(string s, float cx, float y, Color color, bool shadow = true)
        {
            Draw(s, cx - Width(s) / 2f, y, color, shadow);
        }

        private static void DrawRaw(string s, float x, float y, Color color)
        {
            if (NeedsFallback(s))
            {
                var st = Fallback();
                st.normal.textColor = color;
                GUI.Label(new Rect(x * McGui.Scale, (y - 1.5f) * McGui.Scale, 4000, 12 * McGui.Scale), s, st);
                return;
            }
            var old = GUI.color;
            GUI.color = color;
            foreach (var c in s)
            {
                if (c != ' ' && glyphs.TryGetValue(c, out var g))
                {
                    float w = g.CellW * g.Height / g.CellH;
                    McGui.BlitSized(g.Tex, x, y + 7 - g.Ascent, w, g.Height, g.U, g.V, g.CellW, g.CellH, g.Tex.width, g.Tex.height);
                }
                x += Adv(c);
            }
            GUI.color = old;
        }
    }
}
