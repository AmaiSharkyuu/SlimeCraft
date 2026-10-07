using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// How any stack looks in 3D (in the hand, on the ground, in Steve's hand) and as a 16x16 GUI icon:
    ///  * Minecraft items use their real item model: item/generated layers extruded one pixel thick, or the block
    ///    model for block items, with the model's "display" transforms (firstperson_righthand, ground, gui...);
    ///  * Subnautica items are their inventory sprite treated exactly like a Minecraft item/generated model, so a
    ///    titanium chunk or a peeper looks like a Minecraft item in the hand.
    /// Icons of block models are rendered once with a hidden camera, like Minecraft's GUI item renderer.
    /// </summary>
    public static class ItemRender
    {
        private static readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>();
        private static readonly Dictionary<string, Texture2D> icons = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<Identifiable.Id, Texture2D> snTextures = new Dictionary<Identifiable.Id, Texture2D>();
        private static Material cutoutMat, translucentMat;

        public static Material Cutout => cutoutMat != null ? cutoutMat : (cutoutMat = BlockWorld.MakeMat(1));
        public static Material Translucent => translucentMat != null ? translucentMat : (translucentMat = BlockWorld.MakeMat(2));

        /// <summary>Item model name for a stack, following overrides (bow pull, crossbow, elytra broken, trident throwing...).</summary>
        public static string ModelName(ItemStack s, Dictionary<string, float> predicates = null)
        {
            var d = s.Item;
            string model = d.Model ?? ("item/" + d.Id);
            if (predicates != null || d.Id == "elytra")
            {
                var pred = predicates ?? new Dictionary<string, float>();
                if (d.Id == "elytra") pred["broken"] = s.Damage >= d.Durability - 1 ? 1 : 0;
                foreach (var ov in d.Overrides)
                {
                    bool ok = true;
                    foreach (var kv in ov.Key) if (!pred.TryGetValue(kv.Key, out var v) || v < kv.Value) { ok = false; break; }
                    if (ok) model = ov.Value;
                }
            }
            return model;
        }

        public static JObject Model(string name) => McData.Models.TryGetValue(name, out var m) ? m : null;

        /// <summary>Leather armour, potions, tipped arrows, spawn eggs and fireworks tint their layers (ItemColors).</summary>
        private static Color[] LayerTints(ItemStack s)
        {
            if (!s.IsMc) return null;
            var id = s.Item.Id;
            if (id.StartsWith("leather_")) return new[] { Hex(s.Color >= 0 ? s.Color : 0xA06540), Color.white };
            if (id == "potion" || id == "splash_potion" || id == "lingering_potion" || id == "tipped_arrow")
            {
                var c = Hex(PotionColor(s.Potion));
                return id == "tipped_arrow" ? new[] { c, Color.white } : new[] { c, Color.white };
            }
            if (id.EndsWith("_spawn_egg")) return new[] { Color.white, Color.white };
            return null;
        }

        public static Color Hex(int v) => new Color(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f, 1f);

        /// <summary>PotionUtil.getColor: the average colour of the potion's effects, weighted by level.</summary>
        public static int PotionColor(string potion)
        {
            if (potion == null || !McData.Potions.TryGetValue(potion, out var effects) || effects.Count == 0) return 0x385DC6;
            float r = 0, g = 0, b = 0, n = 0;
            foreach (var e in effects)
            {
                if (!McData.Effects.TryGetValue(e.Effect, out var def)) continue;
                int c = def.Color, k = e.Amplifier + 1;
                r += k * ((c >> 16) & 255) / 255f; g += k * ((c >> 8) & 255) / 255f; b += k * (c & 255) / 255f; n += k;
            }
            if (n == 0) return 0x385DC6;
            return ((int)(r / n * 255) << 16) | ((int)(g / n * 255) << 8) | (int)(b / n * 255);
        }

        // ------------------------------------------------------------------------------------------------ meshes

        public static Mesh MeshFor(ItemStack s, string modelName, out bool blockModel, out bool translucent)
        {
            blockModel = false; translucent = false;
            if (s.IsSr)
            {
                var key = "sn:" + s.Actor;
                if (!meshes.TryGetValue(key, out var mm) || mm == null)
                {
                    var tex = SnTexture(s.Actor);
                    mm = tex != null ? ExtrudeTexture(tex) : null;
                    if (mm != null) meshes[key] = mm;
                }
                return mm;
            }
            var m = Model(modelName);
            if (m == null) return null;
            var tints = LayerTints(s);
            var mkey = modelName + (tints != null ? "#" + s.Color + s.Potion : "");
            if (meshes.TryGetValue(mkey, out var mesh) && mesh != null)
            {
                blockModel = m["elements"] != null;
                translucent = blockModel && s.Item.Block != null && s.Item.Block.Layer == "translucent";
                return mesh;
            }
            if (m["elements"] != null)
            {
                blockModel = true;
                int tint = s.Item.Block != null ? s.Item.Block.Tint : -1;
                if (s.Item.Id == "grass_block" || s.Item.Id.EndsWith("leaves") || s.Item.Id == "vine" || s.Item.Id == "lily_pad") tint = s.Item.Block?.Tint ?? -1;
                mesh = ModelBaker.ModelMesh(modelName, tint, out _);
                translucent = s.Item.Block != null && s.Item.Block.Layer == "translucent";
            }
            else if (m["layers"] is JArray layers)
            {
                var list = new List<string>();
                foreach (var l in layers) list.Add((string)l);
                mesh = ModelBaker.GeneratedMesh(list, tints);
            }
            else if (m["builtin"] != null && (string)m["builtin"] == "entity")
            {
                // chests, shields, tridents, beds, banners, skulls in the hand: a cube of the particle texture
                mesh = ModelBaker.ModelMesh("block/stone", -1, out _);
            }
            meshes[mkey] = mesh;
            return mesh;
        }

        /// <summary>A Subnautica sprite copied out of its atlas into a readable texture (the SN atlas is GPU-only).</summary>
        public static Texture2D SnTexture(Identifiable.Id t)
        {
            if (snTextures.TryGetValue(t, out var tex) && tex != null) return tex;
            var sp = SrItems.Icon(t);
            tex = null;
            if (sp != null && sp.texture != null)
            {
                try { tex = RasterSprite(sp, 32, true); }
                catch (System.Exception e) { Plugin.Log.LogWarning("icon " + t + ": " + e.Message); }
                if (tex != null) tex.name = "sn:" + t;
            }
            // Subnautica loads its sprite atlases asynchronously: only remember a result once there is one
            snTextures[t] = tex;
            return tex;
        }

        private static Material spriteMat;

        /// <summary>
        /// Renders a sprite through its own vertices and UVs (Subnautica packs its icons tightly in sprite atlases,
        /// so they cannot be cut out as rectangles), into an n x n point-filtered texture with Minecraft's hard alpha.
        /// </summary>
        private static readonly Dictionary<Identifiable.Id, Texture2D> snIcons = new Dictionary<Identifiable.Id, Texture2D>();

        /// <summary>Inventory icon of a Subnautica item at 128 px with soft edges, as crisp as in Subnautica's own UI.</summary>
        public static Texture2D SnIconHi(Identifiable.Id t)
        {
            if (snIcons.TryGetValue(t, out var tex) && tex != null) return tex;
            var sp = SrItems.Icon(t);
            tex = null;
            if (sp != null && sp.texture != null)
            {
                try { tex = RasterSprite(sp, 128, false); tex.filterMode = FilterMode.Bilinear; tex.name = "sn icon:" + t; }
                catch (System.Exception e) { Plugin.Log.LogWarning("icon " + t + ": " + e.Message); }
            }
            snIcons[t] = tex;
            return tex;
        }

        public static Texture2D RasterSprite(Sprite sp, int n, bool hard)
        {
            if (spriteMat == null) { spriteMat = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default")); Object.DontDestroyOnLoad(spriteMat); }
            var verts = sp.vertices; var uvs = sp.uv; var tris = sp.triangles;
            var b = sp.bounds;
            float size = Mathf.Max(b.size.x, b.size.y);
            var c = b.center;
            int res = n * 4;
            var rt = RenderTexture.GetTemporary(res, res, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, new Color(0, 0, 0, 0));
            spriteMat.mainTexture = sp.texture;
            spriteMat.SetPass(0);
            GL.PushMatrix();
            GL.LoadOrtho();
            GL.Begin(GL.TRIANGLES);
            GL.Color(Color.white);
            for (int i = 0; i < tris.Length; i++)
            {
                var v = verts[tris[i]];
                GL.TexCoord2(uvs[tris[i]].x, uvs[tris[i]].y);
                GL.Vertex3((v.x - c.x) / size + 0.5f, (v.y - c.y) / size + 0.5f, 0);
            }
            GL.End();
            GL.PopMatrix();
            var full = new Texture2D(res, res, TextureFormat.RGBA32, false);
            full.ReadPixels(new Rect(0, 0, res, res), 0, 0);
            full.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            // down-sample to n x n pixels (average of each 4x4 block) so it looks like a Minecraft item
            var src = full.GetPixels();
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    Color sum = Color.clear; float aSum = 0;
                    for (int dy = 0; dy < 4; dy++)
                        for (int dx = 0; dx < 4; dx++)
                        {
                            var p = src[(y * 4 + dy) * res + x * 4 + dx];
                            sum += new Color(p.r * p.a, p.g * p.a, p.b * p.a, 0);
                            aSum += p.a;
                        }
                    float a = aSum / 16f;
                    var col = aSum > 0 ? new Color(sum.r / aSum, sum.g / aSum, sum.b / aSum, hard ? (a > 0.4f ? 1f : 0f) : a) : Color.clear;
                    px[y * n + x] = col;
                }
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels(px);
            tex.Apply();
            Object.Destroy(full);
            Object.DontDestroyOnLoad(tex);
            return tex;
        }

        /// <summary>Extrudes a standalone texture (UVs over that texture, not the atlas).</summary>
        private static Mesh ExtrudeTexture(Texture2D src)
        {
            var mb = new MeshBuilder();
            int w = src.width, h = src.height;
            var px = src.GetPixels32();
            bool Op(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && px[y * w + x].a > 0;
            float z0 = 7.5f / 16f - 0.5f, z1 = 8.5f / 16f - 0.5f;
            Vector3 P(float x, float y, float z) => new Vector3(x / w - 0.5f, y / h - 0.5f, z);
            Vector2 U(float x, float y) => new Vector2(x / w, y / h);
            var c0 = Color.white;
            mb.Raw(P(0, 0, z0), P(0, h, z0), P(w, h, z0), P(w, 0, z0), Vector3.back, U(0, 0), U(0, h), U(w, h), U(w, 0), c0);
            mb.Raw(P(w, 0, z1), P(w, h, z1), P(0, h, z1), P(0, 0, z1), Vector3.forward, U(w, 0), U(w, h), U(0, h), U(0, 0), c0);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (!Op(x, y)) continue;
                    var c = U(x + 0.5f, y + 0.5f);
                    if (!Op(x - 1, y)) mb.Raw(P(x, y, z1), P(x, y + 1, z1), P(x, y + 1, z0), P(x, y, z0), Vector3.left, c, c, c, c, c0);
                    if (!Op(x + 1, y)) mb.Raw(P(x + 1, y, z0), P(x + 1, y + 1, z0), P(x + 1, y + 1, z1), P(x + 1, y, z1), Vector3.right, c, c, c, c, c0);
                    if (!Op(x, y - 1)) mb.Raw(P(x, y, z0), P(x + 1, y, z0), P(x + 1, y, z1), P(x, y, z1), Vector3.down, c, c, c, c, c0);
                    if (!Op(x, y + 1)) mb.Raw(P(x, y + 1, z1), P(x + 1, y + 1, z1), P(x + 1, y + 1, z0), P(x, y + 1, z0), Vector3.up, c, c, c, c, c0);
                }
            return mb.Build("sn item");
        }

        private static readonly Dictionary<Identifiable.Id, Material> snMats = new Dictionary<Identifiable.Id, Material>();

        /// <summary>
        /// A visual for a stack under `parent`, already placed with the model's display transform for `context`
        /// ("firstperson_righthand", "thirdperson_righthand", "ground", "gui", "fixed", "head").
        /// The returned root's local transform is the display transform; callers position the parent.
        /// </summary>
        public static GameObject Build(ItemStack s, Transform parent, int layer, string context, Dictionary<string, float> predicates = null)
        {
            var root = new GameObject("item " + s.Id) { layer = layer };
            root.transform.SetParent(parent, false);
            if (s.IsMc && (s.Item.Id == "trident" || s.Item.Id == "shield") && context != "gui")
            {
                // builtin/entity items with a real entity model of their own
                SpecialItems.Build(s, root.transform, layer, context);
                return root;
            }
            if (s.IsSr && context != "gui")
            {
                var model = SrModels.Make(s.Actor, root.transform, layer);
                if (model != null)
                {
                    SnDisplay(root.transform, context);
                    return root;
                }
            }
            string modelName = s.IsMc ? ModelName(s, predicates) : null;
            var mesh = MeshFor(s, modelName, out bool blockModel, out bool translucent);
            if (mesh == null) return root;
            var inner = new GameObject("mesh") { layer = layer };
            inner.transform.SetParent(root.transform, false);
            inner.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = inner.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            if (s.IsSr)
            {
                if (!snMats.TryGetValue(s.Actor, out var mat) || mat == null || mat.mainTexture == null) { mat = BlockWorld.MakeMat(1, SnTexture(s.Actor)); snMats[s.Actor] = mat; }
                mr.sharedMaterial = mat;
            }
            else mr.sharedMaterial = translucent ? Translucent : Cutout;
            if (s.IsMc && s.Enchanted) Glint.Add(inner);

            // the model's own display transform (ItemRenderer applies it about the model centre)
            var m = s.IsMc ? Model(modelName) : Model("item/iron_ingot");
            var disp = m?["display"]?[context] as JObject;
            ApplyDisplay(root.transform, disp, context);
            return root;
        }

        /// <summary>
        /// How a Subnautica model (scaled to one block) sits: in first person pointing away from the camera like a
        /// held tool, in third person along Steve's grip, on the ground at a quarter size like Minecraft's drops.
        /// Values are in Unity space, applied on top of Minecraft's hand transforms.
        /// </summary>
        private static void SnDisplay(Transform t, string context)
        {
            switch (context)
            {
                case "firstperson_righthand":
                    t.localPosition = new Vector3(0.02f, 0.06f, 0.08f);
                    t.localRotation = Quaternion.Euler(-8f, -12f, 0f);
                    t.localScale = Vector3.one * 0.62f;
                    break;
                case "thirdperson_righthand":
                    t.localPosition = new Vector3(0f, 0.05f, 0.1f);
                    t.localRotation = Quaternion.Euler(0f, 0f, 0f);
                    t.localScale = Vector3.one * 0.55f;
                    break;
                case "ground":
                    t.localPosition = Vector3.up * 0.05f;
                    t.localRotation = Quaternion.identity;
                    t.localScale = Vector3.one * 0.4f;
                    break;
                default:
                    t.localPosition = Vector3.zero;
                    t.localRotation = Quaternion.identity;
                    t.localScale = Vector3.one * 0.5f;
                    break;
            }
        }

        /// <summary>Transformation.apply: translate (pixels/16, x mirrored for the left hand), rotate (XYZ), scale; in Unity space.</summary>
        public static void ApplyDisplay(Transform t, JObject disp, string context)
        {
            Vector3 rot = Vector3.zero, tr = Vector3.zero, sc = Vector3.one;
            if (disp != null)
            {
                if (disp["rotation"] is JArray r) rot = new Vector3((float)r[0], (float)r[1], (float)r[2]);
                if (disp["translation"] is JArray a) tr = new Vector3((float)a[0], (float)a[1], (float)a[2]) / 16f;
                if (disp["scale"] is JArray s) sc = new Vector3((float)s[0], (float)s[1], (float)s[2]);
            }
            // Minecraft space -> Unity: z mirrored; rotations about x and y flip sign, about z stays
            t.localPosition = new Vector3(tr.x, tr.y, -tr.z);
            var q = Quaternion.AngleAxis(-rot.x, Vector3.right) * Quaternion.AngleAxis(-rot.y, Vector3.up) * Quaternion.AngleAxis(rot.z, Vector3.forward);
            // Minecraft composes rotationXYZ as x then y then z applied to the vector: R = Rx * Ry * Rz
            t.localRotation = q;
            t.localScale = sc;
        }

        // ------------------------------------------------------------------------------------------------ icons

        private static Camera iconCam;
        private static GameObject iconStage;

        /// <summary>The 16x16-slot icon of a stack: the item texture itself for flat items, a rendered block for block models.</summary>
        public static Texture Icon(ItemStack s, out Rect uv)
        {
            uv = new Rect(0, 0, 1, 1);
            if (s.IsSr) return SnIconHi(s.Actor);
            var modelName = ModelName(s);
            var m = Model(modelName);
            if (m == null) return null;
            if (m["layers"] is JArray layers && LayerTints(s) == null)
            {
                // flat item: draw the first layer straight from the atlas (fast path); overlays are rare and handled by rendering
                if (layers.Count == 1)
                {
                    var tex = (string)layers[0];
                    var r = Atlas.Rect(tex);
                    var src = Atlas.Source(tex);
                    if (src != null && src.height > src.width) { } // animated: first frame is already in the atlas slot
                    uv = r;
                    return Atlas.Texture;
                }
            }
            var key = modelName + (s.Color >= 0 ? "#" + s.Color : "") + (s.Potion ?? "");
            if (icons.TryGetValue(key, out var ic) && ic != null) return ic;
            ic = RenderIcon(s, modelName);
            icons[key] = ic;
            return ic;
        }

        private static Texture2D RenderIcon(ItemStack s, string modelName)
        {
            const int size = 64;
            if (iconStage == null)
            {
                iconStage = new GameObject("SlimeCraft icon stage");
                Object.DontDestroyOnLoad(iconStage);
                iconStage.transform.position = new Vector3(0, -20000, 0);
                var camGo = new GameObject("icon cam");
                camGo.transform.SetParent(iconStage.transform, false);
                camGo.transform.localPosition = new Vector3(0, 0, -10);
                iconCam = camGo.AddComponent<Camera>();
                iconCam.orthographic = true;
                iconCam.orthographicSize = 0.5f;
                iconCam.nearClipPlane = 0.1f;
                iconCam.farClipPlane = 20f;
                iconCam.clearFlags = CameraClearFlags.SolidColor;
                iconCam.backgroundColor = new Color(0, 0, 0, 0);
                iconCam.cullingMask = 1 << 29;
                iconCam.enabled = false;
                var lightGo = new GameObject("icon light");
                lightGo.transform.SetParent(iconStage.transform, false);
                lightGo.transform.rotation = Quaternion.Euler(40, -30, 0);
                var l = lightGo.AddComponent<Light>();
                l.type = LightType.Directional;
                l.intensity = 1.1f;
                l.cullingMask = 1 << 29;
            }
            var holder = new GameObject("icon holder") { layer = 29 };
            holder.transform.SetParent(iconStage.transform, false);
            var vis = Build(s, holder.transform, 29, "gui");
            foreach (var r in vis.GetComponentsInChildren<Renderer>()) r.gameObject.layer = 29;
            var rt = RenderTexture.GetTemporary(size, size, 16, RenderTextureFormat.ARGB32);
            iconCam.targetTexture = rt;
            var oldAmbient = RenderSettings.ambientLight;
            var oldMode = RenderSettings.ambientMode;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.55f);
            iconCam.Render();
            RenderSettings.ambientLight = oldAmbient;
            RenderSettings.ambientMode = oldMode;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, name = "icon " + modelName };
            tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            iconCam.targetTexture = null;
            RenderTexture.ReleaseTemporary(rt);
            Object.DestroyImmediate(holder);
            Object.DontDestroyOnLoad(tex);
            return tex;
        }
    }

    /// <summary>The enchantment glint: a second, additive scrolling pass over enchanted items (ItemRenderer's glint).</summary>
    public static class Glint
    {
        private static Material mat;

        public static void Add(GameObject meshObject)
        {
            var mr = meshObject.GetComponent<MeshRenderer>();
            if (mr == null) return;
            if (mat == null)
            {
                var sh = Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Sprites/Default");
                mat = new Material(sh) { name = "mc:glint" };
                var tex = McTex.Get("misc/enchanted_glint_item");
                tex.wrapMode = TextureWrapMode.Repeat;
                tex.filterMode = FilterMode.Bilinear;
                mat.mainTexture = tex;
                if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", new Color(0.5f, 0.25f, 0.8f, 0.5f));
                mat.mainTextureScale = new Vector2(0.16f, 0.16f);
                Object.DontDestroyOnLoad(mat);
            }
            var mats = new List<Material>(mr.sharedMaterials) { mat };
            mr.sharedMaterials = mats.ToArray();
        }

        public static void Animate()
        {
            if (mat == null) return;
            // Minecraft scrolls the glint over 3 s and 4.875 s on two axes
            float t = Time.time;
            mat.mainTextureOffset = new Vector2((t / 3f) % 1f, (t / 4.875f) % 1f);
        }
    }

    /// <summary>Items drawn by Minecraft's BuiltinModelItemRenderer: the trident and the shield, with their entity models.</summary>
    public static class SpecialItems
    {
        public static void Build(ItemStack s, Transform parent, int layer, string context)
        {
            if (s.Item.Id == "trident")
            {
                // TridentEntityModel (32x32)
                var root = new MPart("trident");
                root.Add(new MPart("pole").Box(0, 6, -0.5f, 2, -0.5f, 1, 25, 1)
                    .Add(new MPart("base").Box(4, 0, -1.5f, 0, -0.5f, 3, 2, 1))
                    .Add(new MPart("left_spike").Box(4, 3, -2.5f, -3, -0.5f, 1, 4, 1))
                    .Add(new MPart("middle_spike").Box(0, 0, -0.5f, -4, -0.5f, 1, 4, 1))
                    .Add(new MPart("right_spike").Box(4, 3, 1.5f, -3, -0.5f, 1, 4, 1, 0, true)));
                var holder = new GameObject("trident") { layer = layer };
                holder.transform.SetParent(parent, false);
                var mat = BlockWorld.MakeMat(1, McTex.Get("entity/trident"));
                root.Build(holder.transform, mat, 32, 32, layer);
                // BuiltinModelItemRenderer: scale(1,-1,-1) then the trident model, held by the shaft
                holder.transform.localScale = new Vector3(1, -1, -1) * 1f;
                holder.transform.localRotation = Quaternion.Euler(0, 0, 180);
                holder.transform.localPosition = new Vector3(0, 0.1f, 0);
                var disp = ItemRender.Model("item/trident_in_hand")?["display"]?[context] as JObject;
                ItemRender.ApplyDisplay(parent, disp, context);
            }
            else
            {
                // ShieldEntityModel (64x64)
                var root = new MPart("shield");
                root.Add(new MPart("plate").Box(0, 0, -6, -11, -2, 12, 22, 1));
                root.Add(new MPart("handle").Box(26, 0, -1, -3, -1, 2, 6, 6));
                var holder = new GameObject("shield") { layer = layer };
                holder.transform.SetParent(parent, false);
                var mat = BlockWorld.MakeMat(1, McTex.Get("entity/shield_base_nopattern"));
                root.Build(holder.transform, mat, 64, 64, layer);
                holder.transform.localScale = new Vector3(1, -1, -1);
                holder.transform.localRotation = Quaternion.Euler(0, 0, 180);
                var disp = ItemRender.Model("item/shield")?["display"]?[context] as JObject;
                ItemRender.ApplyDisplay(parent, disp, context);
            }
        }
    }
}
