using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace SlimeCraft
{
    public enum BlockRender { Opaque, Cutout, Translucent }

    public enum BlockShape { Cube, Column, Slab, Stairs, Fence, Carpet, Cross, Torch, Chest, Shulker, Barrel }

    public class BlockDef
    {
        public int Index;
        public string Id, Name, Sound, Tab;
        public BlockRender Render;
        public BlockShape Shape;
        public bool Survival;
        public float Hardness;
        public int Light;
        public string Top, Side, Bottom, Front;
        public Texture2D Icon;

        public bool HasFront => Front != Side && (Shape == BlockShape.Cube);
        public bool IsContainer => Shape == BlockShape.Chest || Shape == BlockShape.Shulker || Shape == BlockShape.Barrel;
        public bool IsSprite => Shape == BlockShape.Cross || Shape == BlockShape.Torch;
        public bool Walkable => Shape == BlockShape.Cross || Shape == BlockShape.Torch;
        public bool FullCube => Shape == BlockShape.Cube || Shape == BlockShape.Column || Shape == BlockShape.Barrel || Shape == BlockShape.Shulker;
        public int ContainerSize => IsContainer ? 27 : 0;
    }

    /// <summary>
    /// Minecraft blocks living in Slime Rancher's world: one GameObject per block on the world's 1 m grid,
    /// drawn with Slime Rancher's own Paintlight shaders so its sun, night and fog light them like the rest
    /// of the ranch, with colliders that the player, slimes and raycasts all respect.
    /// </summary>
    public static class Blocks
    {
        public static readonly List<BlockDef> Defs = new List<BlockDef>();
        public static readonly Dictionary<string, BlockDef> ById = new Dictionary<string, BlockDef>();

        public class Placed
        {
            public BlockDef Def;
            public Vector3Int Cell;
            // Cube with a front / chest: yaw quarter turns. Column: axis 0 y, 1 x, 2 z. Barrel: facing 0 up, 1 down,
            // 2..5 horizontal. Slab: 0 bottom, 1 top, 2 double. Stairs: yaw + 4 when upside down. Torch: 0 floor, 1..4 wall.
            public int Rot;
            public GameObject Go;
            public Transform Lid;
            public ItemStack[] Contents;
            public float LidOpen, LidTarget;
        }

        public static readonly Dictionary<Vector3Int, Placed> World = new Dictionary<Vector3Int, Placed>();
        private static GameObject root;
        private static readonly Dictionary<string, Material> mats = new Dictionary<string, Material>();

        public static void LoadDefs()
        {
            Defs.Clear(); ById.Clear();
            var p = Path.Combine(McAssets.Root, "blocks.txt");
            if (!File.Exists(p)) { Plugin.Log.LogError("blocks.txt missing"); return; }
            foreach (var line in File.ReadAllLines(p, System.Text.Encoding.UTF8))
            {
                var f = line.Split('|');
                if (f.Length < 13) continue;
                var d = new BlockDef
                {
                    Index = Defs.Count, Id = f[0], Name = f[1], Sound = f[3],
                    Hardness = float.Parse(f[4], CultureInfo.InvariantCulture), Light = int.Parse(f[5]),
                    Top = f[6], Side = f[7], Bottom = f[8], Front = f[9], Tab = f[11], Survival = f[12] == "1",
                };
                d.Render = f[2] == "cutout" || f[2] == "cross" || f[2] == "torch" ? BlockRender.Cutout : f[2] == "translucent" ? BlockRender.Translucent : BlockRender.Opaque;
                switch (f[10])
                {
                    case "column": d.Shape = BlockShape.Column; break;
                    case "slab": d.Shape = BlockShape.Slab; break;
                    case "stairs": d.Shape = BlockShape.Stairs; break;
                    case "fence": d.Shape = BlockShape.Fence; break;
                    case "carpet": d.Shape = BlockShape.Carpet; break;
                    case "cross": d.Shape = BlockShape.Cross; break;
                    case "torch": d.Shape = BlockShape.Torch; break;
                    case "chest": d.Shape = BlockShape.Chest; break;
                    case "shulker": d.Shape = BlockShape.Shulker; break;
                    case "barrel": d.Shape = BlockShape.Barrel; break;
                    default: d.Shape = BlockShape.Cube; break;
                }
                Defs.Add(d);
                ById[d.Id] = d;
            }
            foreach (var d in Defs)
            {
                try { d.Icon = IconRenderer.Render(d); }
                catch (System.Exception e) { Plugin.Log.LogWarning("icon " + d.Id + ": " + e.Message); }
            }
            Plugin.Log.LogInfo("loaded " + Defs.Count + " Minecraft blocks");
        }

        // ---------------------------------------------------------------- materials

        /// <summary>Which _Depth texture the Paintlight shader gets: "tex" (the block texture itself), "white", "gray".</summary>
        public static string DepthMode = "gray";
        public static bool UseLegacy;

        /// <summary>Finds a shader that is loaded in the game even when Shader.Find cannot see it.</summary>
        public static Shader FindShader(string name)
        {
            var s = Shader.Find(name);
            if (s != null) return s;
            foreach (var sh in Resources.FindObjectsOfTypeAll<Shader>()) if (sh.name == name) return sh;
            return null;
        }

        private static Texture2D flat;

        public static Texture2D TexOf(string tex) => McAssets.Tex((tex.StartsWith("entity/") ? "" : "blocks/") + tex + ".png");

        /// <summary>
        /// Slime Rancher paints its world with its own "Paintlight" shaders, which carry the game's lighting and
        /// fog; Unity's stock shaders get washed out by that fog. So Minecraft textures go into Paintlight/Basic
        /// (opaque) and Paintlight/Cutout (glass, leaves, plants, torches). Translucent glass and ice use the
        /// sprite shader, which keeps their see-through tint.
        /// </summary>
        public static Material Mat(string tex, BlockRender render)
        {
            var key = tex + "|" + render + "|" + DepthMode + UseLegacy;
            if (mats.TryGetValue(key, out var m) && m != null) return m;
            var t = TexOf(tex);
            if (render == BlockRender.Translucent)
            {
                m = new Material(Shader.Find("Sprites/Default")) { mainTexture = t };
                m.renderQueue = 3000;
            }
            else m = MakeMat(t, render == BlockRender.Cutout);
            m.name = "mc:" + tex;
            Object.DontDestroyOnLoad(m);
            mats[key] = m;
            return m;
        }

        public static Material MakeMat(Texture2D t, bool cutout)
        {
            Shader sh = UseLegacy ? null : FindShader(cutout ? "SR/Paintlight/Cutout" : "SR/Paintlight/Basic");
            Material m;
            if (sh != null)
            {
                m = new Material(sh);
                m.SetTexture("_PrimaryTex", t);
                Texture depth = t;
                if (DepthMode != "tex")
                {
                    if (flat == null)
                    {
                        flat = new Texture2D(4, 4);
                        var c = DepthMode == "white" ? Color.white : new Color(0.5f, 0.5f, 0.5f, 1f);
                        var px = new Color[16];
                        for (int i = 0; i < 16; i++) px[i] = c;
                        flat.SetPixels(px); flat.Apply();
                        Object.DontDestroyOnLoad(flat);
                    }
                    depth = flat;
                }
                m.SetTexture("_Depth", depth);
                if (cutout) { m.SetFloat("_Cutoff", 0.5f); m.EnableKeyword("_ALPHATEST_ON"); m.renderQueue = 2450; }
            }
            else
            {
                m = new Material(Shader.Find(cutout ? "Legacy Shaders/Transparent/Diffuse" : "Legacy Shaders/Diffuse")) { mainTexture = t };
            }
            m.mainTexture = t;
            return m;
        }

        public static void ResetMaterials()
        {
            mats.Clear();
            flat = null;
            foreach (var p in World.Values) Build(p);
        }

        /// <summary>Materials for the 4 submeshes every block mesh has: top, bottom, side, front.</summary>
        public static Material[] MatsFor(BlockDef d)
        {
            var r = d.Render;
            if (d.Shape == BlockShape.Chest || d.Shape == BlockShape.Shulker)
            {
                var m = Mat(d.Side, BlockRender.Cutout);
                return new[] { m, m, m, m };
            }
            return new[] { Mat(d.Top, r), Mat(d.Bottom, r), Mat(d.Side, r), Mat(d.Front, r) };
        }

        // ---------------------------------------------------------------- meshes

        /// <summary>Builds meshes from boxes in block pixels (0..16 = one block, centred on the origin once built).</summary>
        public class MeshBuilder
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Vector3> N = new List<Vector3>();
            public readonly List<Vector2> UV = new List<Vector2>();
            public readonly List<int>[] Tris;

            public MeshBuilder(int submeshes)
            {
                Tris = new List<int>[submeshes];
                for (int i = 0; i < submeshes; i++) Tris[i] = new List<int>();
            }

            /// <summary>Quad from four corners given clockwise as seen from outside, which is Unity's front-face winding.</summary>
            public void Quad(int sub, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
            {
                int i = V.Count;
                V.Add(a); V.Add(b); V.Add(c); V.Add(d);
                N.Add(n); N.Add(n); N.Add(n); N.Add(n);
                UV.Add(ua); UV.Add(ub); UV.Add(uc); UV.Add(ud);
                Tris[sub].Add(i); Tris[sub].Add(i + 1); Tris[sub].Add(i + 2);
                Tris[sub].Add(i); Tris[sub].Add(i + 2); Tris[sub].Add(i + 3);
            }

            /// <summary>
            /// Box from min to max in pixels. uv(face) gives the texture rectangle (u0,v0,u1,v1) in 16ths of the
            /// texture, v going down like Minecraft; a negative u0 skips the face.
            /// Faces: 0 up, 1 down, 2 north(-z), 3 south(+z), 4 west(-x), 5 east(+x).
            /// </summary>
            public void Box(Vector3 min, Vector3 max, System.Func<int, Vector4> uv, System.Func<int, int> sub)
            {
                Vector3 a = min / 16f - Vector3.one * 0.5f, b = max / 16f - Vector3.one * 0.5f;
                Vector2 U(Vector4 r, float x, float y) => new Vector2(Mathf.Lerp(r.x, r.z, x) / 16f, 1f - Mathf.Lerp(r.y, r.w, y) / 16f);
                for (int f = 0; f < 6; f++)
                {
                    var r = uv(f);
                    if (r.x < 0) continue;
                    int s = sub(f);
                    switch (f)
                    {
                        case 0:
                            Quad(s, new Vector3(a.x, b.y, b.z), new Vector3(b.x, b.y, b.z), new Vector3(b.x, b.y, a.z), new Vector3(a.x, b.y, a.z), Vector3.up,
                                U(r, 0, 1), U(r, 1, 1), U(r, 1, 0), U(r, 0, 0));
                            break;
                        case 1:
                            Quad(s, new Vector3(a.x, a.y, a.z), new Vector3(b.x, a.y, a.z), new Vector3(b.x, a.y, b.z), new Vector3(a.x, a.y, b.z), Vector3.down,
                                U(r, 0, 0), U(r, 1, 0), U(r, 1, 1), U(r, 0, 1));
                            break;
                        case 2:
                            Quad(s, new Vector3(b.x, a.y, a.z), new Vector3(a.x, a.y, a.z), new Vector3(a.x, b.y, a.z), new Vector3(b.x, b.y, a.z), Vector3.back,
                                U(r, 1, 1), U(r, 0, 1), U(r, 0, 0), U(r, 1, 0));
                            break;
                        case 3:
                            Quad(s, new Vector3(a.x, a.y, b.z), new Vector3(b.x, a.y, b.z), new Vector3(b.x, b.y, b.z), new Vector3(a.x, b.y, b.z), Vector3.forward,
                                U(r, 1, 1), U(r, 0, 1), U(r, 0, 0), U(r, 1, 0));
                            break;
                        case 4:
                            Quad(s, new Vector3(a.x, a.y, a.z), new Vector3(a.x, a.y, b.z), new Vector3(a.x, b.y, b.z), new Vector3(a.x, b.y, a.z), Vector3.left,
                                U(r, 1, 1), U(r, 0, 1), U(r, 0, 0), U(r, 1, 0));
                            break;
                        case 5:
                            Quad(s, new Vector3(b.x, a.y, b.z), new Vector3(b.x, a.y, a.z), new Vector3(b.x, b.y, a.z), new Vector3(b.x, b.y, b.z), Vector3.right,
                                U(r, 1, 1), U(r, 0, 1), U(r, 0, 0), U(r, 1, 0));
                            break;
                    }
                }
            }

            /// <summary>A box whose faces show the parts of the block texture it covers, like Minecraft's default UVs.</summary>
            public void UvLockedBox(Vector3 a, Vector3 b, int front)
            {
                Box(a, b, f =>
                {
                    switch (f)
                    {
                        case 0: return new Vector4(a.x, a.z, b.x, b.z);
                        case 1: return new Vector4(a.x, 16 - b.z, b.x, 16 - a.z);
                        case 2: return new Vector4(16 - b.x, 16 - b.y, 16 - a.x, 16 - a.y);
                        case 3: return new Vector4(a.x, 16 - b.y, b.x, 16 - a.y);
                        case 4: return new Vector4(a.z, 16 - b.y, b.z, 16 - a.y);
                        default: return new Vector4(16 - b.z, 16 - b.y, 16 - a.z, 16 - a.y);
                    }
                }, f => f == 0 ? 0 : f == 1 ? 1 : f == front ? 3 : 2);
            }

            /// <summary>
            /// Minecraft's entity box UV layout (u, v = top-left of the unfolded box in a texSize skin).
            /// yUp: the model is built upright (chests), where Minecraft's first "top" region is really the bottom.
            /// </summary>
            public void EntityBox(Vector3 min, Vector3 max, int u, int v, int w, int h, int d, float texSize, bool yUp, int sub = 0)
            {
                float k = 16f / texSize; // to 16ths
                Vector4 R(float x0, float y0, float x1, float y1) => new Vector4(x0, y0, x1, y1) * k;
                Box(min, max, f =>
                {
                    switch (f)
                    {
                        case 0: return yUp ? R(u + d + w, v + d, u + d + w + w, v) : R(u + d + w, v, u + d, v + d);
                        case 1: return yUp ? R(u + d, v, u + d + w, v + d) : R(u + d + w + w, v + d, u + d + w, v);
                        case 3: return yUp ? R(u + d, v + d + h, u + d + w, v + d) : R(u + d, v + d, u + d + w, v + d + h);
                        case 2: return yUp ? R(u + d + w + d, v + d + h, u + d + w + d + w, v + d) : R(u + d + w + d, v + d, u + d + w + d + w, v + d + h);
                        case 5: return yUp ? R(u, v + d + h, u + d, v + d) : R(u, v + d, u + d, v + d + h);
                        default: return yUp ? R(u + d + w, v + d + h, u + d + w + d, v + d) : R(u + d + w, v + d, u + d + w + d, v + d + h);
                    }
                }, f => sub);
            }

            public Mesh Build()
            {
                var m = new Mesh();
                m.SetVertices(V); m.SetNormals(N); m.SetUVs(0, UV);
                m.subMeshCount = Tris.Length;
                for (int i = 0; i < Tris.Length; i++) m.SetTriangles(Tris[i], i);
                m.RecalculateBounds();
                return m;
            }
        }

        private static readonly Dictionary<string, Mesh> meshCache = new Dictionary<string, Mesh>();

        private static Mesh Cached(string key, System.Func<MeshBuilder, bool> build)
        {
            if (meshCache.TryGetValue(key, out var m) && m != null) return m;
            var mb = new MeshBuilder(4);
            build(mb);
            m = mb.Build();
            m.name = key;
            Object.DontDestroyOnLoad(m);
            meshCache[key] = m;
            return m;
        }

        /// <summary>Full cube with submeshes top, bottom, side, front; the front faces south (+z) before rotation.</summary>
        public static Mesh CubeMesh() => Cached("cube", mb => { mb.UvLockedBox(Vector3.zero, Vector3.one * 16, 3); return true; });

        public static Mesh SlabMesh(bool top) => Cached("slab" + top, mb => { mb.UvLockedBox(new Vector3(0, top ? 8 : 0, 0), new Vector3(16, top ? 16 : 8, 16), 3); return true; });

        public static Mesh CarpetMesh() => Cached("carpet", mb => { mb.UvLockedBox(Vector3.zero, new Vector3(16, 1, 16), 3); return true; });

        /// <summary>Stairs: a bottom slab plus the upper half towards +z (flipped when upside down).</summary>
        public static Mesh StairsMesh(bool upside) => Cached("stairs" + upside, mb =>
        {
            if (!upside) { mb.UvLockedBox(Vector3.zero, new Vector3(16, 8, 16), -1); mb.UvLockedBox(new Vector3(0, 8, 8), new Vector3(16, 16, 16), -1); }
            else { mb.UvLockedBox(new Vector3(0, 8, 0), new Vector3(16, 16, 16), -1); mb.UvLockedBox(new Vector3(0, 0, 8), new Vector3(16, 8, 16), -1); }
            return true;
        });

        /// <summary>Minecraft's fence model: a 4x16x4 post plus two rails towards each connected side.</summary>
        public static Mesh FenceMesh(bool n, bool s, bool w, bool e) => Cached("fence" + n + s + w + e, mb =>
        {
            mb.UvLockedBox(new Vector3(6, 0, 6), new Vector3(10, 16, 10), -1);
            void Rails(Vector3 min, Vector3 max)
            {
                foreach (var y in new[] { 6f, 12f }) mb.UvLockedBox(new Vector3(min.x, y, min.z), new Vector3(max.x, y + 3, max.z), -1);
            }
            if (n) Rails(new Vector3(7, 0, 0), new Vector3(9, 0, 6));
            if (s) Rails(new Vector3(7, 0, 10), new Vector3(9, 0, 16));
            if (w) Rails(new Vector3(0, 0, 7), new Vector3(6, 0, 9));
            if (e) Rails(new Vector3(10, 0, 7), new Vector3(16, 0, 9));
            return true;
        });

        /// <summary>Minecraft's torch: a 2x10x2 stick using the middle of torch.png.</summary>
        public static Mesh TorchMesh() => Cached("torch", mb =>
        {
            mb.Box(new Vector3(7, 0, 7), new Vector3(9, 10, 9),
                f => f == 0 ? new Vector4(7, 6, 9, 8) : f == 1 ? new Vector4(7, 13, 9, 15) : new Vector4(7, 6, 9, 16), f => 2);
            return true;
        });

        /// <summary>Flowers, saplings, grass: two crossed double-sided planes, like Minecraft's "cross" model.</summary>
        public static Mesh CrossMesh() => Cached("cross", mb =>
        {
            float h = 0.5f, o = 0.4f;
            var uv0 = new Vector2(0, 0); var uv1 = new Vector2(1, 0); var uv2 = new Vector2(1, 1); var uv3 = new Vector2(0, 1);
            void Plane(Vector3 p0, Vector3 p1)
            {
                var a = p0 + Vector3.down * h; var b = p1 + Vector3.down * h; var c = p1 + Vector3.up * h; var d = p0 + Vector3.up * h;
                var n = Vector3.Cross(c - a, b - a).normalized;
                mb.Quad(2, a, d, c, b, n, uv0, uv3, uv2, uv1);
                mb.Quad(2, b, c, d, a, -n, uv1, uv2, uv3, uv0);
            }
            Plane(new Vector3(-o, 0, -o), new Vector3(o, 0, o));
            Plane(new Vector3(-o, 0, o), new Vector3(o, 0, -o));
            return true;
        });

        /// <summary>The chest: base and lid from the entity texture (64x64), lock on the +z side.</summary>
        public static Mesh ChestBase() => Cached("chestBase", mb =>
        {
            mb.EntityBox(new Vector3(1, 0, 1), new Vector3(15, 10, 15), 0, 19, 14, 10, 14, 64, true);
            return true;
        });

        /// <summary>Lid and lock, built around the hinge at the back top edge (y 9, z 1) so it can swing open.</summary>
        public static Mesh ChestLid() => Cached("chestLid", mb =>
        {
            // MeshBuilder centres pixel 8 on the origin; offset so the hinge (8, 9, 1) lands on the origin.
            var off = new Vector3(0, -1, 7);
            mb.EntityBox(new Vector3(1, 9, 1) + off, new Vector3(15, 14, 15) + off, 0, 0, 14, 5, 14, 64, true);
            mb.EntityBox(new Vector3(7, 7, 15) + off, new Vector3(9, 11, 16) + off, 0, 0, 2, 4, 1, 64, true);
            return true;
        });

        /// <summary>Shulker box: the 16x8 base and the 16x12 lid that lifts when it opens.</summary>
        public static Mesh ShulkerBase() => Cached("shulkerBase", mb => { mb.EntityBox(new Vector3(0, 0, 0), new Vector3(16, 8, 16), 0, 28, 16, 8, 16, 64, false); return true; });
        public static Mesh ShulkerLid() => Cached("shulkerLid", mb => { mb.EntityBox(new Vector3(0, 4, 0), new Vector3(16, 16, 16), 0, 0, 16, 12, 16, 64, false); return true; });

        // ---------------------------------------------------------------- world

        private static GameObject Root()
        {
            if (root == null) root = new GameObject("SlimeCraft Blocks");
            return root;
        }

        public static Placed Place(BlockDef d, Vector3Int cell, int rot, bool sound = true, ItemStack[] contents = null)
        {
            if (World.ContainsKey(cell)) return null;
            var p = new Placed { Def = d, Cell = cell, Rot = rot };
            if (d.IsContainer) p.Contents = contents ?? new ItemStack[27];
            World[cell] = p;
            Build(p);
            foreach (var nb in Neighbours4(cell)) if (World.TryGetValue(nb, out var q) && q.Def.Shape == BlockShape.Fence) Build(q);
            if (sound) McSound.Play(PlaceSound(d), Center(cell), 1f, 0.8f);
            Persist.Dirty = true;
            return p;
        }

        public static void SetRot(Placed p, int rot)
        {
            p.Rot = rot;
            Build(p);
            Persist.Dirty = true;
        }

        public static Placed Remove(Vector3Int cell, bool sound = true)
        {
            if (!World.TryGetValue(cell, out var p)) return null;
            World.Remove(cell);
            if (p.Go != null) Object.Destroy(p.Go);
            foreach (var nb in Neighbours4(cell)) if (World.TryGetValue(nb, out var q) && q.Def.Shape == BlockShape.Fence) Build(q);
            // Torches and plants standing on (or hanging off) this block fall with it, like in Minecraft.
            foreach (var nb in new[] { cell + Vector3Int.up, cell + new Vector3Int(0, 0, -1), cell + new Vector3Int(0, 0, 1), cell + new Vector3Int(-1, 0, 0), cell + new Vector3Int(1, 0, 0) })
                if (World.TryGetValue(nb, out var t) && t.Def.Walkable && Support(t) == cell)
                {
                    Remove(nb, false);
                    if (!GameMode.Creative) ItemEntity.Spawn(new ItemStack(t.Def, 1), Center(nb), Vector3.up * 2f);
                }
            if (sound) McSound.Play(BreakSound(p.Def), Center(cell), 1f, 0.8f);
            Persist.Dirty = true;
            return p;
        }

        public static void Clear()
        {
            foreach (var p in World.Values) if (p.Go != null) Object.Destroy(p.Go);
            World.Clear();
            if (root != null) Object.Destroy(root);
            root = null;
        }

        public static Vector3 Center(Vector3Int c) => new Vector3(c.x + 0.5f, c.y + 0.5f, c.z + 0.5f);

        public static Vector3Int CellAt(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), Mathf.FloorToInt(p.z));

        private static IEnumerable<Vector3Int> Neighbours4(Vector3Int c)
        {
            yield return c + new Vector3Int(0, 0, -1);
            yield return c + new Vector3Int(0, 0, 1);
            yield return c + new Vector3Int(-1, 0, 0);
            yield return c + new Vector3Int(1, 0, 0);
        }

        public static Vector3Int Support(Placed t)
        {
            if (t.Def.Shape == BlockShape.Torch)
                switch (t.Rot)
                {
                    case 1: return t.Cell + new Vector3Int(0, 0, 1);
                    case 2: return t.Cell + new Vector3Int(0, 0, -1);
                    case 3: return t.Cell + new Vector3Int(1, 0, 0);
                    case 4: return t.Cell + new Vector3Int(-1, 0, 0);
                }
            return t.Cell + Vector3Int.down;
        }

        private static bool FenceConnects(Vector3Int c)
        {
            return World.TryGetValue(c, out var p) && (p.Def.Shape == BlockShape.Fence || (p.Def.FullCube && p.Def.Render == BlockRender.Opaque));
        }

        private static readonly Vector3[] BarrelDirs = { Vector3.up, Vector3.down, Vector3.back, Vector3.forward, Vector3.left, Vector3.right };

        private static void Build(Placed p)
        {
            if (p.Go != null) Object.Destroy(p.Go);
            var d = p.Def;
            var go = new GameObject("mc " + d.Id);
            go.transform.SetParent(Root().transform, false);
            go.transform.position = Center(p.Cell);
            go.layer = 0;
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = MatsFor(d);
            go.AddComponent<BlockTag>().Cell = p.Cell;
            p.Lid = null;
            void Col(Vector3 c, Vector3 size, bool trigger = false) { var bc = go.AddComponent<BoxCollider>(); bc.center = c; bc.size = size; bc.isTrigger = trigger; }

            switch (d.Shape)
            {
                case BlockShape.Fence:
                {
                    bool n = FenceConnects(p.Cell + new Vector3Int(0, 0, -1)), s = FenceConnects(p.Cell + new Vector3Int(0, 0, 1));
                    bool w = FenceConnects(p.Cell + new Vector3Int(-1, 0, 0)), e = FenceConnects(p.Cell + new Vector3Int(1, 0, 0));
                    mf.sharedMesh = FenceMesh(n, s, w, e);
                    // Minecraft fences are 1.5 blocks tall for collision, so nothing hops over them.
                    Col(new Vector3(0, 0.25f, 0), new Vector3(0.25f, 1.5f, 0.25f));
                    if (n) Col(new Vector3(0, 0.25f, -0.3125f), new Vector3(0.25f, 1.5f, 0.375f));
                    if (s) Col(new Vector3(0, 0.25f, 0.3125f), new Vector3(0.25f, 1.5f, 0.375f));
                    if (w) Col(new Vector3(-0.3125f, 0.25f, 0), new Vector3(0.375f, 1.5f, 0.25f));
                    if (e) Col(new Vector3(0.3125f, 0.25f, 0), new Vector3(0.375f, 1.5f, 0.25f));
                    break;
                }
                case BlockShape.Torch:
                {
                    mf.sharedMesh = TorchMesh();
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    if (p.Rot > 0)
                    {
                        // Wall torch: leans 22.5 degrees away from the wall it hangs on, like Minecraft's wall_torch model.
                        var away = (Vector3)(p.Cell - Support(p));
                        go.transform.position = Center(p.Cell) - away * 0.3125f + Vector3.up * 0.2f;
                        go.transform.rotation = Quaternion.AngleAxis(22.5f, Vector3.Cross(Vector3.up, away));
                    }
                    Col(new Vector3(0, -0.2f, 0), new Vector3(0.2f, 0.6f, 0.2f), true); // walk through, but still aim at it
                    break;
                }
                case BlockShape.Cross:
                    mf.sharedMesh = CrossMesh();
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    Col(Vector3.zero, new Vector3(0.8f, 1f, 0.8f), true);
                    break;
                case BlockShape.Slab:
                    if (p.Rot == 2) { mf.sharedMesh = CubeMesh(); Col(Vector3.zero, Vector3.one); }
                    else { mf.sharedMesh = SlabMesh(p.Rot == 1); Col(new Vector3(0, p.Rot == 1 ? 0.25f : -0.25f, 0), new Vector3(1, 0.5f, 1)); }
                    break;
                case BlockShape.Carpet:
                    mf.sharedMesh = CarpetMesh();
                    Col(new Vector3(0, -0.5f + 1 / 32f, 0), new Vector3(1, 1 / 16f, 1));
                    break;
                case BlockShape.Stairs:
                {
                    bool up = p.Rot >= 4;
                    mf.sharedMesh = StairsMesh(up);
                    go.transform.rotation = Quaternion.Euler(0, (p.Rot & 3) * 90f, 0);
                    Col(new Vector3(0, up ? 0.25f : -0.25f, 0), new Vector3(1, 0.5f, 1));
                    Col(new Vector3(0, up ? -0.25f : 0.25f, 0.25f), new Vector3(1, 0.5f, 0.5f));
                    break;
                }
                case BlockShape.Chest:
                {
                    mf.sharedMesh = ChestBase();
                    go.transform.rotation = Quaternion.Euler(0, p.Rot * 90f, 0);
                    var lid = new GameObject("lid").transform;
                    lid.SetParent(go.transform, false);
                    lid.localPosition = new Vector3(0, 9f / 16f - 0.5f, 1f / 16f - 0.5f);
                    lid.gameObject.AddComponent<MeshFilter>().sharedMesh = ChestLid();
                    lid.gameObject.AddComponent<MeshRenderer>().sharedMaterials = mr.sharedMaterials;
                    p.Lid = lid;
                    Col(new Vector3(0, -1f / 16f, 0), new Vector3(14f / 16f, 14f / 16f, 14f / 16f));
                    break;
                }
                case BlockShape.Shulker:
                {
                    mf.sharedMesh = ShulkerBase();
                    var lid = new GameObject("lid").transform;
                    lid.SetParent(go.transform, false);
                    lid.gameObject.AddComponent<MeshFilter>().sharedMesh = ShulkerLid();
                    lid.gameObject.AddComponent<MeshRenderer>().sharedMaterials = mr.sharedMaterials;
                    p.Lid = lid;
                    Col(Vector3.zero, Vector3.one);
                    break;
                }
                case BlockShape.Barrel:
                    mf.sharedMesh = CubeMesh();
                    go.transform.rotation = Quaternion.FromToRotation(Vector3.up, BarrelDirs[Mathf.Clamp(p.Rot, 0, 5)]);
                    Col(Vector3.zero, Vector3.one);
                    break;
                case BlockShape.Column:
                    mf.sharedMesh = CubeMesh();
                    if (p.Rot == 1) go.transform.rotation = Quaternion.Euler(0, 0, 90);
                    else if (p.Rot == 2) go.transform.rotation = Quaternion.Euler(90, 0, 0);
                    Col(Vector3.zero, Vector3.one);
                    break;
                default:
                    mf.sharedMesh = CubeMesh();
                    if (d.HasFront) go.transform.rotation = Quaternion.Euler(0, p.Rot * 90f, 0);
                    Col(Vector3.zero, Vector3.one);
                    break;
            }

            if (d.Light > 0)
            {
                // Block light: a warm point light the size of Minecraft's light falloff (level 15 reaches ~14 blocks).
                var lg = new GameObject("light");
                lg.transform.SetParent(go.transform, false);
                lg.transform.localPosition = d.Shape == BlockShape.Torch ? new Vector3(0, 0.2f, 0) : Vector3.zero;
                var l = lg.AddComponent<Light>();
                l.type = LightType.Point;
                l.range = d.Light * 0.9f;
                l.intensity = d.Shape == BlockShape.Torch ? 1.3f : 1.6f;
                l.color = d.Id.Contains("soul") || d.Id == "sea_lantern" ? new Color(0.7f, 0.9f, 1f) : new Color(1f, 0.85f, 0.6f);
                l.shadows = LightShadows.None;
            }
            p.Go = go;
            ApplyLid(p);
        }

        /// <summary>Opens or closes a container's lid a little each frame (chest lids swing, shulker lids rise and turn).</summary>
        public static void AnimateLids(float dt)
        {
            foreach (var p in World.Values)
            {
                if (p.Lid == null || Mathf.Approximately(p.LidOpen, p.LidTarget)) continue;
                p.LidOpen = Mathf.MoveTowards(p.LidOpen, p.LidTarget, dt * 10f / 2f);
                ApplyLid(p);
            }
        }

        private static void ApplyLid(Placed p)
        {
            if (p.Lid == null) return;
            float t = 1f - Mathf.Pow(1f - p.LidOpen, 3f); // Minecraft eases chest lids with 1 - (1-x)^3
            if (p.Def.Shape == BlockShape.Chest) p.Lid.localRotation = Quaternion.Euler(-t * 90f, 0, 0);
            else
            {
                p.Lid.localPosition = new Vector3(0, t * 0.5f, 0);
                p.Lid.localRotation = Quaternion.Euler(0, t * 270f, 0);
            }
        }

        // ---------------------------------------------------------------- sounds

        public static string StepSound(BlockDef d) => "step/" + d.Sound;
        public static string PlaceSound(BlockDef d) => d.Sound == "glass" ? "step/stone" : "dig/" + d.Sound;
        public static string BreakSound(BlockDef d) => d.Sound == "glass" ? "random/glass" : "dig/" + d.Sound;
        public static string HitSound(BlockDef d) => d.Sound == "glass" ? "step/stone" : "step/" + d.Sound;

        // ---------------------------------------------------------------- save

        public static string Serialize()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var p in World.Values)
            {
                sb.Append(p.Cell.x).Append(' ').Append(p.Cell.y).Append(' ').Append(p.Cell.z).Append(' ').Append(p.Def.Id).Append(' ').Append(p.Rot);
                if (p.Contents != null) sb.Append(' ').Append(ItemStack.SaveList(p.Contents));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        public static void Deserialize(IEnumerable<string> lines)
        {
            Clear();
            foreach (var l in lines)
            {
                var f = l.Split(' ');
                if (f.Length < 5 || !ById.TryGetValue(f[3], out var d)) continue;
                var c = new Vector3Int(int.Parse(f[0]), int.Parse(f[1]), int.Parse(f[2]));
                var p = new Placed { Def = d, Cell = c, Rot = int.Parse(f[4]) };
                if (d.IsContainer) p.Contents = f.Length > 5 ? ItemStack.LoadList(f[5], 27) : new ItemStack[27];
                World[c] = p;
            }
            foreach (var p in World.Values) Build(p);
        }
    }

    /// <summary>Marks a GameObject as a Minecraft block so raycasts can find which cell they hit.</summary>
    public class BlockTag : MonoBehaviour
    {
        public Vector3Int Cell;
    }

    /// <summary>
    /// Renders Minecraft's inventory view of a block (the 30/225 degree isometric view with its three shaded faces)
    /// in software, once per block, so icons never depend on the scene's lighting.
    /// </summary>
    public static class IconRenderer
    {
        private const int Size = 64;

        private struct Face
        {
            public Vector3 O, U, V; // origin and edges in model space (0..1 cube)
            public Texture2D Tex;
            public Rect Uv;         // normalised texture rect, y down
            public float Shade;
        }

        public static Texture2D Render(BlockDef d)
        {
            // Plants and torches are flat item sprites in Minecraft's inventory.
            if (d.IsSprite) return Blocks.TexOf(d.Side);
            var faces = new List<Face>();
            Texture2D T(string n) => Blocks.TexOf(n);
            Rect P16(float x0, float y0, float x1, float y1) => new Rect(x0 / 16f, y0 / 16f, (x1 - x0) / 16f, (y1 - y0) / 16f);

            // Model space is Minecraft's (x east, y up, z south). The GUI view shows the top, the east face on the
            // left and the north face (where block models put their "front") on the right.
            void Box(Vector3 a, Vector3 b, Texture2D top, Rect topUv, Texture2D side, Rect sideUv, Texture2D front, Rect frontUv)
            {
                a /= 16f; b /= 16f;
                faces.Add(new Face { O = new Vector3(a.x, b.y, a.z), U = new Vector3(b.x - a.x, 0, 0), V = new Vector3(0, 0, b.z - a.z), Tex = top, Uv = topUv, Shade = 1f });
                faces.Add(new Face { O = new Vector3(b.x, b.y, b.z), U = new Vector3(0, 0, a.z - b.z), V = new Vector3(0, a.y - b.y, 0), Tex = side, Uv = sideUv, Shade = 0.8f });
                faces.Add(new Face { O = new Vector3(b.x, b.y, a.z), U = new Vector3(a.x - b.x, 0, 0), V = new Vector3(0, a.y - b.y, 0), Tex = front, Uv = frontUv, Shade = 0.6f });
            }
            void Locked(Vector3 a, Vector3 b, Texture2D top, Texture2D side, Texture2D front)
            {
                Box(a, b, top, P16(a.x, a.z, b.x, b.z), side, P16(16 - b.z, 16 - b.y, 16 - a.z, 16 - a.y), front, P16(16 - b.x, 16 - b.y, 16 - a.x, 16 - a.y));
            }
            // Entity box UVs (64x64): top, east (left side region) and front regions of the unfolded box.
            void Entity(Vector3 a, Vector3 b, Texture2D t, int u, int v, int w, int h, int dd, bool yUp)
            {
                Rect E(float x0, float y0, float x1, float y1) => new Rect(x0 / 64f, y0 / 64f, (x1 - x0) / 64f, (y1 - y0) / 64f);
                var top = yUp ? E(u + dd + w, v, u + dd + w + w, v + dd) : E(u + dd, v, u + dd + w, v + dd);
                var side = E(u, v + dd, u + dd, v + dd + h);
                var front = E(u + dd, v + dd, u + dd + w, v + dd + h);
                if (yUp) { side = new Rect(side.x, side.yMax, side.width, -side.height); front = new Rect(front.x, front.yMax, front.width, -front.height); }
                Box(a, b, t, top, t, side, t, front);
            }

            switch (d.Shape)
            {
                case BlockShape.Fence:
                {
                    var t = T(d.Side);
                    Locked(new Vector3(6, 0, 0), new Vector3(10, 16, 4), t, t, t);
                    Locked(new Vector3(7, 12, 4), new Vector3(9, 15, 12), t, t, t);
                    Locked(new Vector3(7, 6, 4), new Vector3(9, 9, 12), t, t, t);
                    Locked(new Vector3(6, 0, 12), new Vector3(10, 16, 16), t, t, t);
                    break;
                }
                case BlockShape.Slab: Locked(Vector3.zero, new Vector3(16, 8, 16), T(d.Top), T(d.Side), T(d.Front)); break;
                case BlockShape.Carpet: Locked(Vector3.zero, new Vector3(16, 1, 16), T(d.Top), T(d.Side), T(d.Front)); break;
                case BlockShape.Stairs:
                    Locked(Vector3.zero, new Vector3(16, 8, 16), T(d.Top), T(d.Side), T(d.Side));
                    Locked(new Vector3(0, 8, 8), new Vector3(16, 16, 16), T(d.Top), T(d.Side), T(d.Side));
                    break;
                case BlockShape.Chest:
                {
                    var t = T(d.Side);
                    Entity(new Vector3(1, 0, 1), new Vector3(15, 10, 15), t, 0, 19, 14, 10, 14, true);
                    Entity(new Vector3(1, 9, 1), new Vector3(15, 14, 15), t, 0, 0, 14, 5, 14, true);
                    Entity(new Vector3(7, 7, 0), new Vector3(9, 11, 1), t, 0, 0, 2, 4, 1, true);
                    break;
                }
                case BlockShape.Shulker:
                {
                    var t = T(d.Side);
                    Entity(Vector3.zero, new Vector3(16, 8, 16), t, 0, 28, 16, 8, 16, false);
                    Entity(new Vector3(0, 4, 0), new Vector3(16, 16, 16), t, 0, 0, 16, 12, 16, false);
                    break;
                }
                default:
                    Locked(Vector3.zero, Vector3.one * 16, T(d.Top), T(d.Side), T(d.Front));
                    break;
            }

            var img = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[Size * Size];
            var depth = new float[Size * Size];
            for (int i = 0; i < depth.Length; i++) depth[i] = float.MaxValue;

            // Minecraft's GUI block transform (rotation [30, 225, 0], scale 0.625), right-handed like Minecraft:
            // first 225 degrees about Y, then 30 about X; the camera looks down -z.
            float cy = Mathf.Cos(225 * Mathf.Deg2Rad), sy = Mathf.Sin(225 * Mathf.Deg2Rad);
            float cx = Mathf.Cos(30 * Mathf.Deg2Rad), sx = Mathf.Sin(30 * Mathf.Deg2Rad);
            float scale = 0.625f * Size;
            Vector3 Pr(Vector3 m)
            {
                m -= Vector3.one * 0.5f;
                float x1 = m.x * cy + m.z * sy, z1 = -m.x * sy + m.z * cy;
                float y2 = m.y * cx - z1 * sx, z2 = m.y * sx + z1 * cx;
                return new Vector3(Size / 2f + x1 * scale, Size / 2f - y2 * scale, -z2); // smaller = nearer
            }

            var pixelCache = new Dictionary<Texture2D, Color32[]>();
            foreach (var f in faces)
            {
                var o = Pr(f.O); var pu = Pr(f.O + f.U) - o; var pv = Pr(f.O + f.V) - o;
                float det = pu.x * pv.y - pu.y * pv.x;
                if (Mathf.Abs(det) < 1e-4f) continue;
                int x0 = Mathf.FloorToInt(Mathf.Min(o.x, o.x + pu.x, o.x + pv.x, o.x + pu.x + pv.x));
                int x1 = Mathf.CeilToInt(Mathf.Max(o.x, o.x + pu.x, o.x + pv.x, o.x + pu.x + pv.x));
                int y0 = Mathf.FloorToInt(Mathf.Min(o.y, o.y + pu.y, o.y + pv.y, o.y + pu.y + pv.y));
                int y1 = Mathf.CeilToInt(Mathf.Max(o.y, o.y + pu.y, o.y + pv.y, o.y + pu.y + pv.y));
                if (!pixelCache.TryGetValue(f.Tex, out var tp)) pixelCache[f.Tex] = tp = f.Tex.GetPixels32();
                int tw = f.Tex.width, th = f.Tex.height;
                for (int y = Mathf.Max(0, y0); y < Mathf.Min(Size, y1); y++)
                    for (int x = Mathf.Max(0, x0); x < Mathf.Min(Size, x1); x++)
                    {
                        float dx = x + 0.5f - o.x, dy = y + 0.5f - o.y;
                        float s = (dx * pv.y - dy * pv.x) / det;
                        float t = (pu.x * dy - pu.y * dx) / det;
                        if (s < 0 || s > 1 || t < 0 || t > 1) continue;
                        float z = o.z + pu.z * s + pv.z * t;
                        int idx = (Size - 1 - y) * Size + x;
                        if (z >= depth[idx]) continue;
                        float tu = f.Uv.x + f.Uv.width * s, tv = f.Uv.y + f.Uv.height * t;
                        int txp = Mathf.Clamp((int)(tu * tw), 0, tw - 1), typ = Mathf.Clamp((int)(tv * th), 0, th - 1);
                        var c = tp[(th - 1 - typ) * tw + txp];
                        if (c.a < 16) continue;
                        depth[idx] = z;
                        px[idx] = new Color32((byte)(c.r * f.Shade), (byte)(c.g * f.Shade), (byte)(c.b * f.Shade), d.Render == BlockRender.Translucent ? c.a : (byte)255);
                    }
            }
            img.SetPixels32(px);
            img.Apply();
            Object.DontDestroyOnLoad(img);
            return img;
        }
    }
}
