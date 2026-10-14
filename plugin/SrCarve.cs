using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Breaking Slime Rancher's own map, like the Mario 64 x Minecraft videos. Mining the ground (or blowing it up)
    /// turns a ball of the Far Far Range around that spot into real Minecraft blocks: grass and dirt on the red
    /// earth, sand on the beaches, stone with ores in the rocks. The scenery's triangles inside that ball are cut out
    /// of its mesh and its collider, so the blocks are what you see and walk on. Digging further makes the
    /// underground appear around every block you break, like Minecraft's caves, down as far as you want.
    /// The cuts, the ground heights and the dug cells are saved with the game.
    /// </summary>
    public static class SrCarve
    {
        public const float MineRadius = 2.6f;

        private class Column { public float Top; public string Kind; }

        /// <summary>
        /// A mesh of the scenery we cut: our own copy with its vertex data in lists (triangles near a hole get split
        /// finer, so a ground made of a few huge triangles can still get a block-sized hole) and its live triangles.
        /// </summary>
        private class Cut
        {
            public Mesh Copy;
            public Matrix4x4 ToWorld;
            public List<Vector3> Pos = new List<Vector3>(), Nrm = new List<Vector3>();
            public List<Vector4> Tan = new List<Vector4>();
            public List<Vector2> Uv = new List<Vector2>(), Uv2 = new List<Vector2>();
            public List<Color> Col = new List<Color>();
            public List<int>[] Tris;
        }

        private static readonly Dictionary<int, Cut> cuts = new Dictionary<int, Cut>();                // by MeshFilter / MeshCollider id
        private static readonly Dictionary<Vector2Int, Column> columns = new Dictionary<Vector2Int, Column>();
        private static readonly HashSet<Vector3Int> dug = new HashSet<Vector3Int>();
        private static readonly List<Vector4> records = new List<Vector4>();                             // x, y, z, radius
        private static readonly HashSet<int> unreadable = new HashSet<int>();
        private static bool warned;

        public static void Clear()
        {
            cuts.Clear(); columns.Clear(); dug.Clear(); records.Clear(); unreadable.Clear();
        }

        // ------------------------------------------------------------------------------------------------ carving

        /// <summary>Turns the scenery within `radius` of `center` into Minecraft blocks. False when there was no ground there.</summary>
        public static bool Carve(Vector3 center, float radius, bool fill = true)
        {
            var terrain = new List<Collider>();
            foreach (var c in Physics.OverlapSphere(center, radius, SrWorld.SolidMask, QueryTriggerInteraction.Ignore))
                if (SrWorld.IsTerrain(c)) terrain.Add(c);
            if (terrain.Count == 0) { Plugin.Log.LogInfo("carve " + center + ": no ground"); return false; }
            int filled = 0, before = BlockWorld.Blocks.Count;

            if (fill)
            {
                int r = Mathf.CeilToInt(radius);
                var cc = BlockWorld.CellAt(center);
                for (int x = cc.x - r; x <= cc.x + r; x++)
                    for (int z = cc.z - r; z <= cc.z + r; z++)
                    {
                        var col = ColumnAt(x, z, center.y + radius);
                        if (col == null) continue;
                        float dx = x + 0.5f - center.x, dz = z + 0.5f - center.z;
                        if (dx * dx + dz * dz > (radius + 0.75f) * (radius + 0.75f)) continue;
                        int top = Mathf.FloorToInt(col.Top);
                        for (int y = Mathf.Min(cc.y - r, top - r); y <= top; y++) Fill(new Vector3Int(x, y, z), col);
                    }
            }

            bool cutAny = false;
            var done = new HashSet<int>();
            foreach (var c in terrain)
            {
                var root = c.GetComponentInParent<LODGroup>() != null ? c.GetComponentInParent<LODGroup>().transform : c.transform;
                foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
                {
                    var mr = mf.GetComponent<MeshRenderer>();
                    if (mr == null || !done.Add(mf.GetInstanceID())) continue;
                    if (!mr.bounds.Intersects(new Bounds(center, Vector3.one * radius * 2f))) continue;
                    if (CutMesh(mf.GetInstanceID(), mf.sharedMesh, mf.transform, center, radius, m => mf.sharedMesh = m)) cutAny = true;
                }
                if (c is BoxCollider box && box.size.y * box.transform.lossyScale.y < 0.5f && c.GetComponent<MeshFilter>() != null)
                {
                    // the ranch ground is a flat plane with a box to stand on: a collider made of the plane itself can be cut
                    var plane = c.GetComponent<MeshCollider>() ?? c.gameObject.AddComponent<MeshCollider>();
                    box.enabled = false;
                    var mfp = c.GetComponent<MeshFilter>();
                    plane.sharedMesh = null;
                    plane.sharedMesh = mfp.sharedMesh;
                    done.Add(plane.GetInstanceID());
                    cutAny = true;
                }
                if (c is MeshCollider mc && done.Add(mc.GetInstanceID()))
                    if (CutMesh(mc.GetInstanceID(), mc.sharedMesh, mc.transform, center, radius, m => { mc.sharedMesh = null; mc.sharedMesh = m; })) cutAny = true;
            }
            if (fill) records.Add(new Vector4(center.x, center.y, center.z, radius));
            filled = BlockWorld.Blocks.Count - before;
            Plugin.Log.LogInfo("carve " + center + " r" + radius + ": " + terrain.Count + " colliders (" + terrain[0].name + "), " + filled + " blocks, cut=" + cutAny);
            if (cutAny) Physics.SyncTransforms();
            Persist.Dirty = true;
            return true;
        }

        /// <summary>Removes the triangles whose middle is inside the ball from (a copy of) a scenery mesh.</summary>
        private static bool CutMesh(int key, Mesh mesh, Transform t, Vector3 center, float radius, System.Action<Mesh> assign)
        {
            if (mesh == null || unreadable.Contains(key)) return false;
            if (!cuts.TryGetValue(key, out var cut))
            {
                if (!mesh.isReadable)
                {
                    unreadable.Add(key);
                    if (!warned) { warned = true; Plugin.Log.LogWarning("scenery mesh " + mesh.name + " is not readable: it stays whole"); }
                    return false;
                }
                cut = new Cut { Copy = Object.Instantiate(mesh), ToWorld = t.localToWorldMatrix };
                cut.Copy.name = mesh.name + " (dug)";
                mesh.GetVertices(cut.Pos); mesh.GetNormals(cut.Nrm); mesh.GetTangents(cut.Tan);
                mesh.GetUVs(0, cut.Uv); mesh.GetUVs(1, cut.Uv2); mesh.GetColors(cut.Col);
                cut.Tris = new List<int>[mesh.subMeshCount];
                for (int s = 0; s < mesh.subMeshCount; s++) { cut.Tris[s] = new List<int>(); mesh.GetTriangles(cut.Tris[s], s); }
                cuts[key] = cut;
            }
            var lc = cut.ToWorld.inverse.MultiplyPoint3x4(center);
            // the ball in the mesh's own space (scenery is scaled about uniformly)
            float scale = Mathf.Max(0.0001f, cut.ToWorld.lossyScale.x);
            float lr = radius / scale, near = (radius + 2f) / scale, fine = 0.7f / scale;
            bool changed = false;
            for (int s = 0; s < cut.Tris.Length; s++)
            {
                var tri = cut.Tris[s];
                // split the big triangles close to the hole until their sides are about a block
                for (int pass = 0; pass < 12; pass++)
                {
                    bool split = false;
                    var next = new List<int>(tri.Count);
                    for (int i = 0; i < tri.Count; i += 3)
                    {
                        int a = tri[i], b = tri[i + 1], c = tri[i + 2];
                        Vector3 pa = cut.Pos[a], pb = cut.Pos[b], pc = cut.Pos[c];
                        float longest = Mathf.Max((pa - pb).sqrMagnitude, Mathf.Max((pb - pc).sqrMagnitude, (pc - pa).sqrMagnitude));
                        if (longest > fine * fine && TriNear(pa, pb, pc, lc, near))
                        {
                            int ab = Mid(cut, a, b), bc = Mid(cut, b, c), ca = Mid(cut, c, a);
                            next.Add(a); next.Add(ab); next.Add(ca);
                            next.Add(ab); next.Add(b); next.Add(bc);
                            next.Add(ca); next.Add(bc); next.Add(c);
                            next.Add(ab); next.Add(bc); next.Add(ca);
                            split = true;
                        }
                        else { next.Add(a); next.Add(b); next.Add(c); }
                    }
                    tri = next;
                    if (!split) break;
                }
                var keep = new List<int>(tri.Count);
                for (int i = 0; i < tri.Count; i += 3)
                {
                    var mid = cut.ToWorld.MultiplyPoint3x4((cut.Pos[tri[i]] + cut.Pos[tri[i + 1]] + cut.Pos[tri[i + 2]]) / 3f);
                    float hx = mid.x - center.x, hz = mid.z - center.z;
                    if (hx * hx + hz * hz <= radius * radius && mid.y > center.y - radius - 1f && mid.y < center.y + radius + 1f) { changed = true; continue; }
                    keep.Add(tri[i]); keep.Add(tri[i + 1]); keep.Add(tri[i + 2]);
                }
                cut.Tris[s] = keep;
            }
            if (!changed) { assign(cut.Copy); return false; }
            var m = cut.Copy;
            m.Clear();
            if (cut.Pos.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(cut.Pos);
            if (cut.Nrm.Count == cut.Pos.Count) m.SetNormals(cut.Nrm);
            if (cut.Tan.Count == cut.Pos.Count) m.SetTangents(cut.Tan);
            if (cut.Uv.Count == cut.Pos.Count) m.SetUVs(0, cut.Uv);
            if (cut.Uv2.Count == cut.Pos.Count) m.SetUVs(1, cut.Uv2);
            if (cut.Col.Count == cut.Pos.Count) m.SetColors(cut.Col);
            m.subMeshCount = cut.Tris.Length;
            for (int s = 0; s < cut.Tris.Length; s++) m.SetTriangles(cut.Tris[s], s, false);
            m.RecalculateBounds();
            assign(m);
            return true;
        }

        /// <summary>Could this triangle reach within `r` of `p`? (bounding box test, cheap and good enough)</summary>
        private static bool TriNear(Vector3 a, Vector3 b, Vector3 c, Vector3 p, float r)
        {
            var min = Vector3.Min(a, Vector3.Min(b, c)) - Vector3.one * r;
            var max = Vector3.Max(a, Vector3.Max(b, c)) + Vector3.one * r;
            return p.x >= min.x && p.y >= min.y && p.z >= min.z && p.x <= max.x && p.y <= max.y && p.z <= max.z;
        }

        /// <summary>A new vertex halfway along an edge, every attribute blended.</summary>
        private static int Mid(Cut c, int a, int b)
        {
            int n = c.Pos.Count;
            c.Pos.Add((c.Pos[a] + c.Pos[b]) * 0.5f);
            if (c.Nrm.Count > a) c.Nrm.Add((c.Nrm[a] + c.Nrm[b]).normalized);
            if (c.Tan.Count > a) c.Tan.Add((c.Tan[a] + c.Tan[b]) * 0.5f);
            if (c.Uv.Count > a) c.Uv.Add((c.Uv[a] + c.Uv[b]) * 0.5f);
            if (c.Uv2.Count > a) c.Uv2.Add((c.Uv2[a] + c.Uv2[b]) * 0.5f);
            if (c.Col.Count > a) c.Col.Add((c.Col[a] + c.Col[b]) * 0.5f);
            return n;
        }

        // ------------------------------------------------------------------------------------------------ ground

        /// <summary>The height and kind of the scenery's ground over a column (measured once, before anything is cut there).</summary>
        private static Column ColumnAt(int x, int z, float from)
        {
            var k = new Vector2Int(x, z);
            if (columns.TryGetValue(k, out var col)) return col;
            var hits = Physics.RaycastAll(new Vector3(x + 0.5f, from + 60f, z + 0.5f), Vector3.down, 200f, SrWorld.SolidMask, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                if (!SrWorld.IsTerrain(h.collider)) continue;
                col = new Column { Top = h.point.y, Kind = SrWorld.SurfaceAt(h) };
                columns[k] = col;
                return col;
            }
            return null;
        }

        private static void Fill(Vector3Int cell, Column col)
        {
            float depth = col.Top - (cell.y + 0.5f);
            if (depth <= -0.5f || BlockWorld.Has(cell) || dug.Contains(cell) || Blocking(cell)) return;
            var b = BlockFor(col, depth, cell);
            if (b != null) BlockWorld.Set(cell, b.Default, false);
        }

        /// <summary>Never bury the player.</summary>
        private static bool Blocking(Vector3Int cell)
        {
            var feet = SrWorld.Feet;
            var box = new Bounds(new Vector3(feet.x, feet.y + 0.9f, feet.z), new Vector3(0.6f, 1.8f, 0.6f));
            return box.Intersects(new Bounds(BlockWorld.Center(cell), Vector3.one));
        }

        /// <summary>Minecraft's layers under each kind of ground: a top block, a few of the soil, then stone with ores.</summary>
        private static BlockDef BlockFor(Column col, float depth, Vector3Int cell)
        {
            string id;
            switch (col.Kind)
            {
                case "sand":
                    id = depth < 3f ? "sand" : depth < 5f ? "sandstone" : null; break;
                case "grass": case "mushroom":
                    id = depth < 1f ? (col.Kind == "mushroom" ? "mycelium" : "grass_block") : depth < 4f ? (Random.value < 0.15f ? "coarse_dirt" : "dirt") : null; break;
                case "dirt":
                    // the Far Far Range's red earth
                    id = depth < 1f ? "red_sand" : depth < 4f ? (Random.value < 0.2f ? "orange_terracotta" : "terracotta") : null; break;
                case "wood": case "log":
                    id = depth < 1f ? "oak_planks" : null; break;
                default:
                    id = null; break;
            }
            if (id != null) return McData.Block(id);
            // stone, with Minecraft's mix of granite, diorite, andesite and ores; deepslate a long way down
            var ore = Ore(depth);
            if (ore != null && McData.Block(ore) != null) return McData.Block(ore);
            if (depth > 40f) return McData.Block("deepslate");
            float n = Mathf.PerlinNoise(cell.x * 0.11f + cell.y * 0.07f, cell.z * 0.11f - cell.y * 0.05f);
            return McData.Block(n < 0.18f ? "granite" : n > 0.84f ? "diorite" : n > 0.74f ? "andesite" : n < 0.24f ? "gravel" : "stone");
        }

        private static string Ore(float depth)
        {
            float r = Random.value, acc = 0;
            bool Pick(float c) { acc += c; return r < acc; }
            bool deep = depth > 40f;
            if (Pick(0.012f)) return deep ? "deepslate_coal_ore" : "coal_ore";
            if (Pick(0.008f)) return deep ? "deepslate_iron_ore" : "iron_ore";
            if (Pick(0.006f)) return deep ? "deepslate_copper_ore" : "copper_ore";
            if (depth > 8f && Pick(0.004f)) return deep ? "deepslate_redstone_ore" : "redstone_ore";
            if (depth > 8f && Pick(0.003f)) return deep ? "deepslate_gold_ore" : "gold_ore";
            if (depth > 12f && Pick(0.002f)) return deep ? "deepslate_lapis_ore" : "lapis_ore";
            if (depth > 16f && Pick(0.0012f)) return deep ? "deepslate_diamond_ore" : "diamond_ore";
            if (depth > 6f && Pick(0.0004f)) return deep ? "deepslate_emerald_ore" : "emerald_ore";
            return null;
        }

        // ------------------------------------------------------------------------------------------------ digging on

        /// <summary>
        /// A block was broken: if it was part of the ground, the cell stays dug and the ground appears around it
        /// (the cells under the scenery's surface get their blocks the moment they could be seen).
        /// </summary>
        public static void Exposed(Vector3Int c)
        {
            if (!columns.TryGetValue(new Vector2Int(c.x, c.z), out var here)) return;
            if (c.y + 0.5f < here.Top) dug.Add(c);
            for (int d = 0; d < 6; d++)
            {
                var n = c + BlockWorld.Offset((Dir)d);
                if (BlockWorld.Has(n) || dug.Contains(n)) continue;
                var k = new Vector2Int(n.x, n.z);
                if (!columns.ContainsKey(k))
                {
                    // a new column under untouched scenery: cut a little of its surface too if the hole reaches it
                    var col = ColumnAt(n.x, n.z, c.y + 4f);
                    if (col == null) continue;
                    if (col.Top - (n.y + 0.5f) < 1.5f) { Carve(BlockWorld.Center(n), 1.8f); continue; }
                }
                Fill(n, columns[k]);
            }
        }

        /// <summary>An explosion in the scenery (TNT, a Boom slime...): the ground there becomes blocks first, so the blast can break them.</summary>
        public static void Blast(Vector3 at, float power)
        {
            if (power < 1f) return;
            Carve(at, Mathf.Min(power * 1.1f, 5f));
        }

        // ------------------------------------------------------------------------------------------------ save

        public static JObject Save()
        {
            var o = new JObject();
            var rec = new JArray();
            foreach (var r in records) rec.Add(new JArray(r.x, r.y, r.z, r.w));
            o["cuts"] = rec;
            var cols = new JArray();
            foreach (var kv in columns) cols.Add(new JArray(kv.Key.x, kv.Key.y, kv.Value.Top, kv.Value.Kind));
            o["columns"] = cols;
            var d = new JArray();
            foreach (var c in dug) d.Add(new JArray(c.x, c.y, c.z));
            o["dug"] = d;
            return o;
        }

        /// <summary>Back in a game: the blocks come back with the world, the scenery gets its holes cut again.</summary>
        public static void Load(JObject o)
        {
            Clear();
            if (o == null) return;
            if (o["columns"] is JArray cols)
                foreach (JArray a in cols) columns[new Vector2Int((int)a[0], (int)a[1])] = new Column { Top = (float)a[2], Kind = (string)a[3] };
            if (o["dug"] is JArray d)
                foreach (JArray a in d) dug.Add(new Vector3Int((int)a[0], (int)a[1], (int)a[2]));
            if (o["cuts"] is JArray rec)
                foreach (JArray a in rec)
                {
                    var v = new Vector4((float)a[0], (float)a[1], (float)a[2], (float)a[3]);
                    records.Add(v);
                    Carve(new Vector3(v.x, v.y, v.z), v.w, false);
                }
        }
    }
}
