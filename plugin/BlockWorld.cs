using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>Data a block keeps beyond its state: chest contents, furnace progress, sign text...</summary>
    public class BlockEntity
    {
        public ItemStack[] Items;
        public int Burn, BurnMax, Cook, CookMax;   // furnaces
        public float Xp;                           // furnace stored experience
        public int Brew, Fuel;                     // brewing stand
        public string[] Text;                      // signs
        public float LidOpen, LidTarget;           // chests
        public JObject Extra;

        public JObject Save()
        {
            var o = new JObject();
            if (Items != null) o["items"] = ItemStack.SaveList(Items);
            if (Burn != 0) o["burn"] = Burn;
            if (BurnMax != 0) o["burnMax"] = BurnMax;
            if (Cook != 0) o["cook"] = Cook;
            if (CookMax != 0) o["cookMax"] = CookMax;
            if (Xp != 0) o["xp"] = Xp;
            if (Brew != 0) o["brew"] = Brew;
            if (Fuel != 0) o["fuel"] = Fuel;
            if (Text != null) o["text"] = new JArray(Text);
            if (Extra != null) o["extra"] = Extra;
            return o;
        }

        public static BlockEntity Load(JObject o, int size)
        {
            var e = new BlockEntity();
            if (o["items"] != null) e.Items = ItemStack.LoadList(o["items"], size);
            e.Burn = (int?)o["burn"] ?? 0; e.BurnMax = (int?)o["burnMax"] ?? 0; e.Cook = (int?)o["cook"] ?? 0; e.CookMax = (int?)o["cookMax"] ?? 0;
            e.Xp = (float?)o["xp"] ?? 0; e.Brew = (int?)o["brew"] ?? 0; e.Fuel = (int?)o["fuel"] ?? 0;
            if (o["text"] != null) e.Text = o["text"].ToObject<string[]>();
            e.Extra = o["extra"] as JObject;
            return e;
        }
    }

    /// <summary>
    /// Minecraft blocks placed in Subnautica's world, on the world's 1 m grid: Unity cell (x, y, z) is the block
    /// whose minimum corner is at (x, y, z) metres. Drawn per 16x16x16 chunk as one mesh (opaque, cutout and
    /// translucent submeshes over the block atlas) with hidden faces culled like Minecraft's renderer, and given
    /// Minecraft's exact collision boxes so the player, creatures, vehicles and raycasts all respect them.
    /// </summary>
    public static class BlockWorld
    {
        public static readonly Dictionary<Vector3Int, BState> Blocks = new Dictionary<Vector3Int, BState>();
        public static readonly Dictionary<Vector3Int, BlockEntity> Entities = new Dictionary<Vector3Int, BlockEntity>();
        private static readonly Dictionary<Vector3Int, Chunk> chunks = new Dictionary<Vector3Int, Chunk>();
        private static readonly HashSet<Vector3Int> dirty = new HashSet<Vector3Int>();
        private static readonly Dictionary<int, Vector3Int> colliderCells = new Dictionary<int, Vector3Int>();
        private static GameObject root;
        private static Material[] mats;
        public const int Layer = 0; // Default: collides with the player, creatures and vehicles

        private class Chunk
        {
            public GameObject Go, Colliders;
            public MeshFilter Mf;
            public MeshRenderer Mr;
            public readonly List<GameObject> Special = new List<GameObject>();
        }

        // ------------------------------------------------------------------------------------------------ materials

        public static Material[] Materials
        {
            get
            {
                if (mats == null || mats[0] == null)
                    mats = new[] { MakeMat(0), MakeMat(1), MakeMat(2) };
                return mats;
            }
        }

        /// <summary>"marmoset" (Subnautica's own MarmosetUBER, lit by its deferred lights and sky), "standard" or "legacy".</summary>
        public static string ShaderMode = "marmoset";
        private static Material marmosetTemplate;
        public static readonly List<Material> AllMats = new List<Material>();

        private static Material MarmosetTemplate()
        {
            if (marmosetTemplate != null) return marmosetTemplate;
            Material best = null; int bestScore = int.MaxValue;
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null || m.shader == null || m.shader.name != "MarmosetUBER" || m.renderQueue >= 2450) continue;
                // the plainest opaque material: fewest keywords, no infection / emission tricks
                int score = m.shaderKeywords.Length * 10;
                foreach (var k in m.shaderKeywords) if (k.Contains("INFECTION") || k.Contains("EMISSION") || k.Contains("FX") || k.Contains("GLASS")) score += 100;
                if (score < bestScore) { bestScore = score; best = m; }
            }
            if (best != null) { marmosetTemplate = new Material(best) { name = "mc:marmoset template" }; Object.DontDestroyOnLoad(marmosetTemplate); }
            return marmosetTemplate;
        }

        /// <summary>A material over the atlas (or `tex`): 0 opaque, 1 cutout, 2 translucent, matte like Minecraft.</summary>
        public static Material MakeMat(int mode, Texture tex = null)
        {
            Material m = null;
            if (ShaderMode == "marmoset" && MarmosetTemplate() != null)
            {
                m = new Material(MarmosetTemplate()) { name = "mc:marmoset" + mode };
                m.shaderKeywords = new string[0];
                m.EnableKeyword("_ZWRITE_ON");
                m.SetTexture("_MainTex", tex != null ? tex : Atlas.Texture);
                m.SetColor("_Color", Color.white);
                m.SetTexture("_BumpMap", null);
                m.SetTexture("_SpecTex", Texture2D.blackTexture);
                m.SetTexture("_Illum", null);
                m.SetFloat("_EnableGlow", 0);
                m.SetFloat("_SpecInt", 0);
                m.SetFloat("_Shininess", 1);
                m.SetFloat("_Fresnel", 0);
                m.SetColor("_SpecColor", Color.black);
                m.SetFloat("_EnableLighting", 1);
                m.SetFloat("_EnableMainMaps", 1);
                m.SetFloat("_EnableInfection", 0);
                m.SetFloat("_MyCullVariable", 2);
                if (mode == 1)
                {
                    m.SetFloat("_EnableCutOff", 1);
                    m.SetFloat("_Cutoff", 0.5f);
                    m.EnableKeyword("MARMO_ALPHA_CLIP");
                    m.renderQueue = 2450;
                }
                else if (mode == 2)
                {
                    m.SetFloat("_Mode", 2);
                    m.SetFloat("_ZWrite", 0);
                    m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    m.DisableKeyword("_ZWRITE_ON");
                    m.EnableKeyword("_ALPHABLEND_ON");
                    m.EnableKeyword("WBOIT");
                    m.renderQueue = 3101;
                }
                else m.renderQueue = 2000;
            }
            else
            {
                m = new Material(Shader.Find(ShaderMode == "legacy" ? "Legacy Shaders/Diffuse" : "Standard")) { name = "mc:std" + mode };
                m.mainTexture = tex != null ? tex : Atlas.Texture;
                m.SetFloat("_Glossiness", 0f);
                m.SetFloat("_Metallic", 0f);
                m.EnableKeyword("_GLOSSYREFLECTIONS_OFF");
                m.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
                if (mode == 1)
                {
                    m.SetFloat("_Mode", 1); m.SetFloat("_Cutoff", 0.5f); m.EnableKeyword("_ALPHATEST_ON");
                    m.SetOverrideTag("RenderType", "TransparentCutout"); m.renderQueue = 2450;
                }
                else if (mode == 2)
                {
                    m.SetFloat("_Mode", 2); m.SetOverrideTag("RenderType", "Transparent");
                    m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    m.SetInt("_ZWrite", 0); m.EnableKeyword("_ALPHABLEND_ON"); m.renderQueue = 3000;
                }
            }
            Object.DontDestroyOnLoad(m);
            AllMats.Add(m);
            return m;
        }

        /// <summary>Debug: switch every Minecraft material's shader at runtime to compare.</summary>
        public static void SwitchShaders(string mode)
        {
            ShaderMode = mode;
            mats = null;
            foreach (var ch in chunks.Values) if (ch.Mr != null) ch.Mr.sharedMaterials = Materials;
        }

        public static int LayerIndex(BlockDef b) => b.Layer == "translucent" ? 2 : b.Layer == "cutout" ? 1 : 0;

        // ------------------------------------------------------------------------------------------------ access

        public static Vector3Int CellAt(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), Mathf.FloorToInt(p.z));
        public static Vector3 Center(Vector3Int c) => new Vector3(c.x + 0.5f, c.y + 0.5f, c.z + 0.5f);
        private static Vector3Int ChunkOf(Vector3Int c) => new Vector3Int(c.x >> 4, c.y >> 4, c.z >> 4);

        public static BState Get(Vector3Int c) => Blocks.TryGetValue(c, out var s) ? s : null;
        public static bool Has(Vector3Int c) => Blocks.ContainsKey(c);

        /// <summary>Unity-space offsets of Minecraft's six directions (north is -z in Minecraft, +z here after the mirror).</summary>
        public static Vector3Int Offset(Dir d)
        {
            switch (d)
            {
                case Dir.Down: return Vector3Int.down;
                case Dir.Up: return Vector3Int.up;
                case Dir.North: return new Vector3Int(0, 0, 1);
                case Dir.South: return new Vector3Int(0, 0, -1);
                case Dir.West: return Vector3Int.left;
                case Dir.East: return Vector3Int.right;
            }
            return Vector3Int.zero;
        }

        public static Dir Opposite(Dir d)
        {
            switch (d)
            {
                case Dir.Down: return Dir.Up;
                case Dir.Up: return Dir.Down;
                case Dir.North: return Dir.South;
                case Dir.South: return Dir.North;
                case Dir.West: return Dir.East;
                case Dir.East: return Dir.West;
            }
            return Dir.None;
        }

        public static string Name(Dir d) => d.ToString().ToLowerInvariant();

        /// <summary>Collision / outline boxes of a state in Unity world space at the given cell.</summary>
        public static Bounds ToWorld(Bounds mc, Vector3Int cell)
        {
            var c = mc.center;
            return new Bounds(new Vector3(cell.x + c.x, cell.y + c.y, cell.z + 1f - c.z), mc.size);
        }

        public static bool IsOpaqueCube(BState s) => s != null && s.Block.Opaque && s.FullCube && s.Block.Layer == "solid";

        // ------------------------------------------------------------------------------------------------ edit

        public static void Set(Vector3Int c, BState s, bool update = true)
        {
            var old = Get(c);
            if (s == null || s.Block.Id == "air") { if (old == null) return; Blocks.Remove(c); }
            else Blocks[c] = s;
            if (old != null && (s == null || old.Block != s.Block)) Entities.Remove(c);
            MarkDirty(c);
            Lights.Changed(c, s);
            Persist.Dirty = true;
            if (update) BlockRules.NeighbourChanged(c);
        }

        public static void Remove(Vector3Int c, bool update = true) => Set(c, null, update);

        public static BlockEntity Entity(Vector3Int c, bool create = false, int size = 0)
        {
            if (Entities.TryGetValue(c, out var e)) return e;
            if (!create) return null;
            e = new BlockEntity();
            if (size > 0) e.Items = new ItemStack[size];
            Entities[c] = e;
            return e;
        }

        private static void MarkDirty(Vector3Int c)
        {
            var ch = ChunkOf(c);
            dirty.Add(ch);
            // A face on the chunk border may need re-culling in the neighbour chunk.
            if ((c.x & 15) == 0) dirty.Add(ch + Vector3Int.left);
            if ((c.x & 15) == 15) dirty.Add(ch + Vector3Int.right);
            if ((c.y & 15) == 0) dirty.Add(ch + Vector3Int.down);
            if ((c.y & 15) == 15) dirty.Add(ch + Vector3Int.up);
            if ((c.z & 15) == 0) dirty.Add(ch + new Vector3Int(0, 0, -1));
            if ((c.z & 15) == 15) dirty.Add(ch + new Vector3Int(0, 0, 1));
        }

        public static void Clear()
        {
            Blocks.Clear();
            Entities.Clear();
            foreach (var ch in chunks.Values) if (ch.Go != null) Object.Destroy(ch.Go);
            chunks.Clear();
            dirty.Clear();
            colliderCells.Clear();
            Lights.Clear();
            if (root != null) Object.Destroy(root);
            root = null;
        }

        public static void RebuildAll()
        {
            foreach (var c in Blocks.Keys) dirty.Add(ChunkOf(c));
        }

        public static bool CellOf(Collider col, out Vector3Int cell) => colliderCells.TryGetValue(col.GetInstanceID(), out cell);

        // ------------------------------------------------------------------------------------------------ meshing

        public static void Update()
        {
            if (dirty.Count == 0) return;
            int budget = 6;
            var done = new List<Vector3Int>();
            foreach (var ch in dirty)
            {
                Build(ch);
                done.Add(ch);
                if (--budget <= 0) break;
            }
            foreach (var d in done) dirty.Remove(d);
        }

        private static void Build(Vector3Int chunkPos)
        {
            if (root == null)
            {
                root = new GameObject("SlimeCraft Blocks");
                Object.DontDestroyOnLoad(root);
            }
            chunks.TryGetValue(chunkPos, out var ch);
            var mb = new MeshBuilder(3);
            var cols = new List<KeyValuePair<Vector3Int, Bounds>>();
            var specials = new List<KeyValuePair<Vector3Int, BState>>();
            var origin = chunkPos * 16;
            for (int x = 0; x < 16; x++)
                for (int y = 0; y < 16; y++)
                    for (int z = 0; z < 16; z++)
                    {
                        var c = origin + new Vector3Int(x, y, z);
                        if (!Blocks.TryGetValue(c, out var s)) continue;
                        foreach (var b in s.Collision) cols.Add(new KeyValuePair<Vector3Int, Bounds>(c, b));
                        if (Special.Handles(s)) { specials.Add(new KeyValuePair<Vector3Int, BState>(c, s)); continue; }
                        int sub = LayerIndex(s.Block);
                        var local = new Vector3(x, y, z);
                        foreach (var r in McData.ModelsFor(s))
                        {
                            foreach (var q in ModelBaker.Bake(r, s.Block.Tint))
                            {
                                if (q.Cull != Dir.None && Culled(s, c, q.Cull)) continue;
                                mb.Add(q, local, sub);
                            }
                        }
                    }

            if (mb.Count == 0 && cols.Count == 0 && specials.Count == 0)
            {
                if (ch != null) { Object.Destroy(ch.Go); chunks.Remove(chunkPos); }
                return;
            }
            if (ch == null)
            {
                ch = new Chunk();
                ch.Go = new GameObject("chunk " + chunkPos);
                ch.Go.layer = Layer;
                ch.Go.transform.SetParent(root.transform, false);
                ch.Go.transform.position = origin;
                ch.Mf = ch.Go.AddComponent<MeshFilter>();
                ch.Mr = ch.Go.AddComponent<MeshRenderer>();
                ch.Mr.sharedMaterials = Materials;
                chunks[chunkPos] = ch;
                // Subnautica's sky / ambient lighting for objects comes from SkyApplier; without it a
                // renderer stays lit by nothing but the sun and looks flat black at depth.
                Sky.Apply(ch.Go, false);
            }
            if (ch.Mf.sharedMesh != null) Object.Destroy(ch.Mf.sharedMesh);
            ch.Mf.sharedMesh = mb.Build("chunk");

            // colliders
            if (ch.Colliders != null)
            {
                foreach (var bc in ch.Colliders.GetComponents<BoxCollider>()) colliderCells.Remove(bc.GetInstanceID());
                Object.Destroy(ch.Colliders);
            }
            ch.Colliders = new GameObject("colliders") { layer = Layer };
            ch.Colliders.transform.SetParent(ch.Go.transform, false);
            foreach (var kv in cols)
            {
                var wb = ToWorld(kv.Value, kv.Key);
                var bc = ch.Colliders.AddComponent<BoxCollider>();
                bc.center = wb.center - (Vector3)origin;
                bc.size = Vector3.Max(wb.size, Vector3.one * 0.01f);
                colliderCells[bc.GetInstanceID()] = kv.Key;
            }

            foreach (var g in ch.Special) if (g != null) Object.Destroy(g);
            ch.Special.Clear();
            foreach (var kv in specials)
            {
                var g = Special.Build(kv.Value, kv.Key);
                if (g == null) continue;
                Sky.Apply(g, false);
                g.transform.SetParent(ch.Go.transform, true);
                ch.Special.Add(g);
            }
        }

        /// <summary>Minecraft's face culling: hidden behind a full opaque cube, or between two identical translucent/cutout blocks (glass, water, leaves stay drawn).</summary>
        private static bool Culled(BState s, Vector3Int c, Dir d)
        {
            if (!Blocks.TryGetValue(c + Offset(d), out var n)) return false;
            if (IsOpaqueCube(n)) return true;
            if (n.Block == s.Block && s.FullCube && n.FullCube && (s.Block.Layer == "translucent" || s.Block.Class == "GlassBlock" || s.Block.Class == "StainedGlassBlock" || s.Block.Class == "FluidBlock")) return true;
            return false;
        }

        // ------------------------------------------------------------------------------------------------ save

        public static JObject Save()
        {
            var palette = new List<string>();
            var index = new Dictionary<BState, int>();
            var cells = new JArray();
            foreach (var kv in Blocks)
            {
                if (!index.TryGetValue(kv.Value, out var i))
                {
                    i = palette.Count;
                    palette.Add(kv.Value.Block.Id + "|" + kv.Value.Key);
                    index[kv.Value] = i;
                }
                cells.Add(kv.Key.x); cells.Add(kv.Key.y); cells.Add(kv.Key.z); cells.Add(i);
            }
            var ents = new JObject();
            foreach (var kv in Entities) ents[kv.Key.x + "," + kv.Key.y + "," + kv.Key.z] = kv.Value.Save();
            return new JObject { ["palette"] = new JArray(palette), ["cells"] = cells, ["entities"] = ents };
        }

        public static void Load(JObject o)
        {
            Clear();
            if (o == null) return;
            var palette = new List<BState>();
            foreach (var p in (JArray)o["palette"])
            {
                var s = ((string)p).Split('|');
                var b = McData.Block(s[0]);
                BState st = null;
                if (b != null && !b.States.TryGetValue(s.Length > 1 ? s[1] : "", out st)) st = b.Default;
                palette.Add(st);
            }
            var cells = (JArray)o["cells"];
            for (int i = 0; i + 3 < cells.Count; i += 4)
            {
                var st = palette[(int)cells[i + 3]];
                if (st == null) continue;
                var c = new Vector3Int((int)cells[i], (int)cells[i + 1], (int)cells[i + 2]);
                Blocks[c] = st;
                Lights.Changed(c, st);
            }
            if (o["entities"] is JObject ents)
                foreach (var kv in ents)
                {
                    var p = kv.Key.Split(',');
                    var c = new Vector3Int(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]));
                    Entities[c] = BlockEntity.Load((JObject)kv.Value, ContainerSize(Get(c)));
                }
            RebuildAll();
        }

        public static int ContainerSize(BState s)
        {
            if (s == null) return 0;
            switch (s.Block.Class)
            {
                case "ChestBlock": case "TrappedChestBlock": case "BarrelBlock": case "ShulkerBoxBlock": return 27;
                case "FurnaceBlock": case "BlastFurnaceBlock": case "SmokerBlock": return 3;
                case "BrewingStandBlock": return 5;
                case "HopperBlock": return 5;
                case "DispenserBlock": case "DropperBlock": return 9;
                default: return 0;
            }
        }
    }

    /// <summary>
    /// Light-emitting blocks (torches, glowstone, sea lanterns...) light Subnautica's world with real point lights
    /// sized by their Minecraft light level, so a torch placed in a dark cave really lights it up. Only the ones
    /// nearest to the player get a light, to stay cheap.
    /// </summary>
    public static class Lights
    {
        private static readonly Dictionary<Vector3Int, int> emitters = new Dictionary<Vector3Int, int>();
        private static readonly Dictionary<Vector3Int, Light> lights = new Dictionary<Vector3Int, Light>();
        private static GameObject root;
        private static float next;
        private const int Max = 24;

        public static void Changed(Vector3Int c, BState s)
        {
            if (s != null && s.Light > 0) emitters[c] = s.Light;
            else { emitters.Remove(c); if (lights.TryGetValue(c, out var l)) { if (l != null) Object.Destroy(l.gameObject); lights.Remove(c); } }
            next = 0;
        }

        public static void Clear()
        {
            emitters.Clear();
            foreach (var l in lights.Values) if (l != null) Object.Destroy(l.gameObject);
            lights.Clear();
        }

        public static void Update(Vector3 player)
        {
            if (Time.time < next) return;
            next = Time.time + 0.5f;
            if (root == null) { root = new GameObject("SlimeCraft Lights"); Object.DontDestroyOnLoad(root); }
            var near = new List<KeyValuePair<Vector3Int, int>>(emitters);
            near.Sort((a, b) => (BlockWorld.Center(a.Key) - player).sqrMagnitude.CompareTo((BlockWorld.Center(b.Key) - player).sqrMagnitude));
            var keep = new HashSet<Vector3Int>();
            for (int i = 0; i < near.Count && i < Max; i++) keep.Add(near[i].Key);
            foreach (var k in new List<Vector3Int>(lights.Keys))
                if (!keep.Contains(k)) { if (lights[k] != null) Object.Destroy(lights[k].gameObject); lights.Remove(k); }
            foreach (var k in keep)
            {
                if (lights.ContainsKey(k)) continue;
                int lvl = emitters[k];
                var go = new GameObject("mc light");
                go.transform.SetParent(root.transform, false);
                go.transform.position = BlockWorld.Center(k);
                var l = go.AddComponent<Light>();
                l.type = LightType.Point;
                l.range = lvl * 1.2f;
                l.intensity = 0.6f + lvl / 15f;
                l.color = new Color(1f, 0.9f, 0.75f);
                l.shadows = LightShadows.None;
                lights[k] = l;
            }
        }
    }
}

namespace SlimeCraft
{
    /// <summary>
    /// Subnautica lights its MarmosetUBER materials with its own sky / ambient probes through a SkyApplier on each
    /// object; Minecraft things get one too so they are lit like the rest of the world (dark at depth, warm in bases).
    /// </summary>
    public static class Sky
    {
        public static void Apply(GameObject go, bool dynamic = true)
        {
            if (go == null) return;
            try
            {
                var sa = go.GetComponent<SkyApplier>() ?? go.AddComponent<SkyApplier>();
                sa.renderers = go.GetComponentsInChildren<Renderer>(true);
                sa.anchorSky = Skies.Auto;
                sa.dynamic = dynamic;
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("SkyApplier: " + e.Message); }
        }
    }
}
