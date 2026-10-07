using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Minecraft's block atlas, rebuilt at load: every texture a block or item model uses, packed into one
    /// point-filtered texture so a whole chunk of blocks is one mesh with one material per render layer.
    /// Tinted faces (grass tops, leaves, water) get their own pre-tinted copy, keyed "tex#rrggbb".
    /// Animated textures (water, lava, sea lantern, prismarine, fire...) are animated by copying the next
    /// frame of their strip into their atlas slot on the GPU, at Minecraft's 20 ticks per second.
    /// </summary>
    public static class Atlas
    {
        public static Texture2D Texture;
        private static readonly Dictionary<string, Rect> rects = new Dictionary<string, Rect>();
        private static readonly Dictionary<string, Texture2D> sources = new Dictionary<string, Texture2D>();
        private static readonly HashSet<string> wanted = new HashSet<string>();
        private static Rect missing;

        private class Anim
        {
            public Texture2D Strip;
            public RectInt Slot;
            public int Frames, Time;
            public int[] Order;
        }

        private static readonly List<Anim> anims = new List<Anim>();
        private static int tick;
        private static float acc;

        /// <summary>CPU copy of a texture (first frame for strips), for item extrusion and icon rendering.</summary>
        public static Texture2D Source(string tex)
        {
            if (tex == null) return null;
            var key = tex;
            if (sources.TryGetValue(key, out var t)) return t;
            int hash = tex.IndexOf('#');
            string path = hash >= 0 ? tex.Substring(0, hash) : tex;
            var file = Path.Combine(McData.Root, "textures", path.Replace('/', Path.DirectorySeparatorChar) + ".png");
            if (!File.Exists(file)) { sources[key] = null; return null; }
            var full = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            full.LoadImage(File.ReadAllBytes(file));
            Texture2D frame = full;
            if (full.height > full.width && McData.Animations.ContainsKey(path))
            {
                frame = new Texture2D(full.width, full.width, TextureFormat.RGBA32, false);
                frame.SetPixels(full.GetPixels(0, full.height - full.width, full.width, full.width));
                frame.Apply();
            }
            if (hash >= 0)
            {
                var c = HexColor(tex.Substring(hash + 1));
                var px = frame.GetPixels();
                for (int i = 0; i < px.Length; i++) px[i] = new Color(px[i].r * c.r, px[i].g * c.g, px[i].b * c.b, px[i].a);
                if (frame == full) frame = new Texture2D(full.width, full.height, TextureFormat.RGBA32, false);
                frame.SetPixels(px);
                frame.Apply();
            }
            frame.filterMode = FilterMode.Point;
            frame.wrapMode = TextureWrapMode.Clamp;
            frame.name = tex;
            Object.DontDestroyOnLoad(frame);
            sources[key] = frame;
            return frame;
        }

        public static Color HexColor(string hex)
        {
            int v = System.Convert.ToInt32(hex, 16);
            return new Color(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f, 1f);
        }

        public static string Tinted(string tex, int tint) => tint < 0 ? tex : tex + "#" + tint.ToString("x6");

        public static void Want(string tex) { if (tex != null) wanted.Add(tex); }

        /// <summary>Collects every texture the models reference (plus tinted variants) and packs them.</summary>
        public static void Build()
        {
            foreach (var kv in McData.Models)
            {
                var m = kv.Value;
                if (m["layers"] is Newtonsoft.Json.Linq.JArray layers) foreach (var l in layers) Want((string)l);
                if (m["particle"] != null) Want((string)m["particle"]);
                if (m["elements"] is Newtonsoft.Json.Linq.JArray els)
                    foreach (var e in els)
                        foreach (var f in (Newtonsoft.Json.Linq.JObject)e["faces"])
                            Want((string)f.Value["tex"]);
            }
            // Tinted copies for every block that has a tint colour.
            foreach (var b in McData.Blocks.Values)
            {
                if (b.Tint < 0) continue;
                foreach (var s in b.States.Values)
                {
                    // every state's models (grass is snowy in some states, vines have five sides...)
                    foreach (var r in McData.ModelsFor(s))
                    {
                        if (!McData.Models.TryGetValue(r.Model, out var m) || !(m["elements"] is Newtonsoft.Json.Linq.JArray els)) continue;
                        foreach (var e in els)
                            foreach (var f in (Newtonsoft.Json.Linq.JObject)e["faces"])
                                if (f.Value["tint"] != null) Want(Tinted((string)f.Value["tex"], b.Tint));
                    }
                }
            }
            // Item layer tints (leather armour, potions, spawn eggs) are applied per item at mesh time instead.
            Want("block/water_still#3f76e4");
            Want("block/water_flow#3f76e4");
            for (int i = 0; i < 10; i++) Want("block/destroy_stage_" + i);

            var list = new List<string>();
            var texs = new List<Texture2D>();
            foreach (var w in wanted)
            {
                var t = Source(w);
                if (t == null) continue;
                list.Add(w);
                texs.Add(t);
            }
            var magenta = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            var mp = new Color32[256];
            for (int i = 0; i < 256; i++) mp[i] = ((i / 8 + i / 128) % 2 == 0) ? new Color32(248, 0, 248, 255) : new Color32(0, 0, 0, 255);
            magenta.SetPixels32(mp); magenta.Apply();
            list.Add("missingno"); texs.Add(magenta);

            Texture = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            var packed = Texture.PackTextures(texs.ToArray(), 0, 8192, false);
            Texture.filterMode = FilterMode.Point;
            Texture.wrapMode = TextureWrapMode.Clamp;
            Texture.name = "mc:atlas";
            Object.DontDestroyOnLoad(Texture);
            for (int i = 0; i < list.Count; i++) rects[list[i]] = packed[i];
            missing = rects["missingno"];

            // Animated strips: remember the slot of each, the strip texture is uploaded once and frames copied on the GPU.
            foreach (var kv in McData.Animations)
            {
                if (!rects.TryGetValue(kv.Key, out var r)) continue;
                var file = Path.Combine(McData.Root, "textures", kv.Key.Replace('/', Path.DirectorySeparatorChar) + ".png");
                var strip = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                strip.LoadImage(File.ReadAllBytes(file));
                Object.DontDestroyOnLoad(strip);
                var order = kv.Value["order"].ToObject<int[]>();
                anims.Add(new Anim
                {
                    Strip = strip,
                    Slot = new RectInt(Mathf.RoundToInt(r.x * Texture.width), Mathf.RoundToInt(r.y * Texture.height), strip.width, strip.width),
                    Frames = (int)kv.Value["frames"], Time = Mathf.Max(1, (int)kv.Value["time"]),
                    Order = order.Length > 0 ? order : null,
                });
            }
            Plugin.Log.LogInfo($"atlas {Texture.width}x{Texture.height}, {list.Count} textures, {anims.Count} animated");
        }

        /// <summary>UV rectangle of a texture in the atlas (the magenta checker when Minecraft has no such file).</summary>
        public static Rect Rect(string tex)
        {
            if (tex != null && rects.TryGetValue(tex, out var r)) return r;
            if (tex != null && tex.IndexOf('#') > 0 && rects.TryGetValue(tex.Substring(0, tex.IndexOf('#')), out r)) return r;
            return missing;
        }

        public static bool Has(string tex) => tex != null && rects.ContainsKey(tex);

        /// <summary>Advance texture animations; called every frame.</summary>
        public static void Animate(float dt)
        {
            if (Texture == null || anims.Count == 0) return;
            acc += dt;
            if (acc < 0.05f) return;
            acc %= 0.05f;
            tick++;
            foreach (var a in anims)
            {
                if (tick % a.Time != 0) continue;
                int n = a.Order != null ? a.Order.Length : a.Frames;
                int step = (tick / a.Time) % n;
                int frame = a.Order != null ? a.Order[step] : step;
                // Strip frames run top to bottom; Unity textures are bottom-up.
                int sy = a.Strip.height - (frame + 1) * a.Strip.width;
                try { Graphics.CopyTexture(a.Strip, 0, 0, 0, sy, a.Slot.width, a.Slot.height, Texture, 0, 0, a.Slot.x, a.Slot.y); }
                catch { }
            }
        }
    }
}
