using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Mining Slime Rancher's own map one block at a time, the way the Mario 64 x Minecraft mod does it.
    /// The scenery is only a skin of triangles with nothing under it. Breaking the ground takes the block-sized cube
    /// you aim at and uses its six faces like knives on those triangles (the visible mesh and the collision mesh):
    /// what is inside the cube is deleted, what is outside is kept and split back into triangles. The ground that
    /// gets uncovered around the hole gets Minecraft faces: cells entirely under the surface become real blocks,
    /// cells the surface passes through get block faces trimmed where they run into the slope.
    /// The block you get depends on the surface (red earth, sand, rock, grass) and, underground, on the depth.
    /// The original surface is kept as a list of triangles so "is this under the ground" still has an answer once
    /// the scenery has holes. The dug cells are saved; the cuts are made again when the game loads.
    /// </summary>
    public static class SrCarve
    {
        // ------------------------------------------------------------------------------------------------ the original surface

        private struct Tri { public Vector3 A, B, C, N; public string Kind; }

        private const float Bucket = 4f;
        private static readonly Dictionary<Vector2Int, List<Tri>> surface = new Dictionary<Vector2Int, List<Tri>>();
        private static readonly HashSet<int> registered = new HashSet<int>();

        private static Vector2Int BucketOf(float x, float z) => new Vector2Int(Mathf.FloorToInt(x / Bucket), Mathf.FloorToInt(z / Bucket));

        /// <summary>Remembers a scenery collider's triangles (before anything is cut out of it).</summary>
        private static void Register(Collider c)
        {
            if (!registered.Add(c.GetInstanceID())) return;
            Mesh mesh = null;
            if (c is MeshCollider mc) mesh = mc.sharedMesh;
            else if (c is BoxCollider box && Thin(box)) mesh = c.GetComponent<MeshFilter>()?.sharedMesh;
            var kind = SrWorld.SurfaceKind(c);
            var tris = new List<Tri>();
            if (mesh != null && mesh.isReadable)
            {
                var v = mesh.vertices;
                var m = c.transform.localToWorldMatrix;
                for (int i = 0; i < v.Length; i++) v[i] = m.MultiplyPoint3x4(v[i]);
                var t = mesh.triangles;
                for (int i = 0; i < t.Length; i += 3) tris.Add(MakeTri(v[t[i]], v[t[i + 1]], v[t[i + 2]], kind));
            }
            else if (c is BoxCollider bc)
            {
                // a solid box (rock, crate...): its top and bottom are enough for "under the surface"
                var m = c.transform.localToWorldMatrix;
                Vector3 P(float x, float y, float z) => m.MultiplyPoint3x4(bc.center + Vector3.Scale(bc.size, new Vector3(x, y, z)));
                tris.Add(MakeTri(P(-.5f, .5f, -.5f), P(-.5f, .5f, .5f), P(.5f, .5f, .5f), kind));
                tris.Add(MakeTri(P(-.5f, .5f, -.5f), P(.5f, .5f, .5f), P(.5f, .5f, -.5f), kind));
                tris.Add(MakeTri(P(-.5f, -.5f, -.5f), P(.5f, -.5f, .5f), P(-.5f, -.5f, .5f), kind));
                tris.Add(MakeTri(P(-.5f, -.5f, -.5f), P(.5f, -.5f, -.5f), P(.5f, -.5f, .5f), kind));
            }
            foreach (var t in tris)
            {
                if (Mathf.Abs(t.N.y) < 0.05f) continue;   // walls never answer a vertical question
                var min = BucketOf(Mathf.Min(t.A.x, Mathf.Min(t.B.x, t.C.x)), Mathf.Min(t.A.z, Mathf.Min(t.B.z, t.C.z)));
                var max = BucketOf(Mathf.Max(t.A.x, Mathf.Max(t.B.x, t.C.x)), Mathf.Max(t.A.z, Mathf.Max(t.B.z, t.C.z)));
                if ((max.x - min.x) * (max.y - min.y) > 40000) continue;
                for (int x = min.x; x <= max.x; x++)
                    for (int z = min.y; z <= max.y; z++)
                    {
                        var k = new Vector2Int(x, z);
                        if (!surface.TryGetValue(k, out var l)) surface[k] = l = new List<Tri>();
                        l.Add(t);
                    }
            }
        }

        private static Tri MakeTri(Vector3 a, Vector3 b, Vector3 c, string kind)
        {
            // Unity: a triangle faces the side its clockwise winding is seen from, normal = (b-a) x (c-a)
            return new Tri { A = a, B = b, C = c, N = Vector3.Cross(b - a, c - a).normalized, Kind = kind };
        }

        private static bool Thin(BoxCollider b) => b.size.y * Mathf.Abs(b.transform.lossyScale.y) < 0.5f;

        private static bool Ours(Collider c) => c.gameObject == faces;

        /// <summary>Registers the scenery colliders around a point.</summary>
        private static void RegisterNear(Vector3 p, float r)
        {
            foreach (var c in Physics.OverlapBox(p, Vector3.one * r, Quaternion.identity, SrWorld.SolidMask, QueryTriggerInteraction.Ignore))
                if (!Ours(c) && SrWorld.IsTerrain(c)) Register(c);
        }

        /// <summary>
        /// How far under the original ground a point is: positive inside the ground (up to the surface above it),
        /// negative in the open air. Looking straight up: the first surface met tells which side we are on.
        /// </summary>
        private static float Depth(Vector3 p, out string kind)
        {
            kind = null;
            if (!surface.TryGetValue(BucketOf(p.x, p.z), out var list)) return -1f;
            float best = float.MaxValue; Tri hit = default; bool found = false;
            foreach (var t in list)
            {
                if (!HeightAt(t, p.x, p.z, out float y) || y < p.y - 0.0005f || y >= best) continue;
                best = y; hit = t; found = true;
            }
            if (!found) return -1f;
            kind = hit.Kind;
            return hit.N.y > 0 ? best - p.y : -(best - p.y) - 0.001f;
        }

        private static float Depth(Vector3 p) => Depth(p, out _);

        /// <summary>The height of a triangle over (x, z), if (x, z) is under it.</summary>
        private static bool HeightAt(Tri t, float x, float z, out float y)
        {
            y = 0;
            float d = (t.B.z - t.C.z) * (t.A.x - t.C.x) + (t.C.x - t.B.x) * (t.A.z - t.C.z);
            if (Mathf.Abs(d) < 1e-7f) return false;
            float u = ((t.B.z - t.C.z) * (x - t.C.x) + (t.C.x - t.B.x) * (z - t.C.z)) / d;
            float v = ((t.C.z - t.A.z) * (x - t.C.x) + (t.A.x - t.C.x) * (z - t.C.z)) / d;
            float w = 1f - u - v;
            const float e = -1e-4f;
            if (u < e || v < e || w < e) return false;
            y = u * t.A.y + v * t.B.y + w * t.C.y;
            return true;
        }

        // ------------------------------------------------------------------------------------------------ the dig

        private static readonly HashSet<Vector3Int> dug = new HashSet<Vector3Int>();
        private static readonly HashSet<Vector3Int> partial = new HashSet<Vector3Int>();
        private static readonly Dictionary<Vector3Int, string> kinds = new Dictionary<Vector3Int, string>();

        private static readonly Vector3[] Corners =
        {
            new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 1),
            new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 1), new Vector3(1, 1, 1),
        };

        /// <summary>How much of a cell is ground: 0 none, 1 some (the surface goes through it), 2 all of it.</summary>
        private static int Fullness(Vector3Int c)
        {
            int inside = 0;
            foreach (var k in Corners) if (Depth(c + k * 0.999f + Vector3.one * 0.0005f) > 0) inside++;
            if (inside == 8) return 2;
            if (inside > 0 || Depth(BlockWorld.Center(c)) > 0) return 1;
            return 0;
        }

        /// <summary>The Minecraft block a cell of ground is made of.</summary>
        public static BlockDef GroundBlock(Vector3Int c)
        {
            RegisterNear(BlockWorld.Center(c), 3f);
            // depth of the cell's top: under 0 the surface runs through the cell (the top layer: grass, red sand)
            float depth = Depth(BlockWorld.Center(c), out var kind) - 0.5f;
            if (kind == null && kinds.TryGetValue(c, out var k)) kind = k;
            if (depth < -0.5f) depth = -0.1f;
            return BlockFor(kind ?? "stone", depth, c);
        }

        /// <summary>
        /// Player mining finished on the scenery at `point`: that cell is cut out of the map, its block drops, the
        /// ground around shows. False when there was nothing of the map to dig there.
        /// </summary>
        public static bool Dig(Vector3 point, Vector3 normal, ItemStack tool)
        {
            var cell = BlockWorld.CellAt(point - normal * 0.05f);
            if (dug.Contains(cell)) cell = BlockWorld.CellAt(point - normal * 0.55f);
            if (dug.Contains(cell) || BlockWorld.Has(cell)) return false;
            var block = GroundBlock(cell);
            BreakCell(cell, true);
            var center = BlockWorld.Center(cell);
            Sounds.Play(Sounds.BlockEvent(block.Sound, "break"), center, 1f, 0.8f);
            Particles.Break(center, block.Default);
            if (!GameMode.Creative)
            {
                foreach (var d in Loot.BlockDrops(block.Default, tool)) ItemEntity.Spawn(d, center, new Vector3(Random.Range(-0.1f, 0.1f), 0.2f, Random.Range(-0.1f, 0.1f)), 0.5f);
                int xp = Loot.BlockXp(block, tool);
                if (xp > 0) XpOrb.Spawn(center, xp);
                if (tool != null && tool.Damageable && tool.Item.IsTool) Inventory.DamageHeld(1);
                Hunger.Exhaust(0.005f);
            }
            return true;
        }

        /// <summary>Cuts a cell out of the scenery, marks it dug and uncovers the ground around it.</summary>
        private static void BreakCell(Vector3Int cell, bool rebuild)
        {
            RegisterNear(BlockWorld.Center(cell), 3f);
            CutCells(new List<Vector3Int> { cell });
            dug.Add(cell);
            partial.Remove(cell);
            Uncover(cell);
            if (rebuild) RebuildFaces();
            Persist.Dirty = true;
        }

        /// <summary>A Minecraft block was broken (by hand, explosion...): if it was ground, the ground around it shows.</summary>
        public static void Exposed(Vector3Int c)
        {
            if (dug.Contains(c)) return;
            RegisterNear(BlockWorld.Center(c), 3f);
            if (Depth(BlockWorld.Center(c)) <= 0 && Fullness(c) == 0) return;
            dug.Add(c);
            Uncover(c);
            RebuildFaces();
            Persist.Dirty = true;
        }

        /// <summary>The six neighbours of an opened cell: whole ground becomes a block, half ground gets trimmed faces.</summary>
        private static void Uncover(Vector3Int c)
        {
            for (int d = 0; d < 6; d++)
            {
                var n = c + BlockWorld.Offset((Dir)d);
                if (dug.Contains(n) || BlockWorld.Has(n) || partial.Contains(n)) continue;
                int full = Fullness(n);
                if (full == 2)
                {
                    if (Blocking(n)) { partial.Add(n); continue; }
                    var b = GroundBlock(n);
                    if (b != null) BlockWorld.Set(n, b.Default, false);
                }
                else if (full == 1) { partial.Add(n); Depth(BlockWorld.Center(n), out var k); if (k != null) kinds[n] = k; }
            }
        }

        /// <summary>Never bury the player.</summary>
        private static bool Blocking(Vector3Int cell)
        {
            var feet = SrWorld.Feet;
            var box = new Bounds(new Vector3(feet.x, feet.y + 0.9f, feet.z), new Vector3(0.6f, 1.8f, 0.6f));
            return box.Intersects(new Bounds(BlockWorld.Center(cell), Vector3.one));
        }

        /// <summary>An explosion in the scenery (TNT, a Boom slime...): the ground within reach is blown out too.</summary>
        public static void Blast(Vector3 at, float power)
        {
            if (power < 1f) return;
            float r = Mathf.Min(power * 0.9f, 4.5f);
            RegisterNear(at, r + 2f);
            var cells = new List<Vector3Int>();
            int ri = Mathf.CeilToInt(r);
            var c0 = BlockWorld.CellAt(at);
            for (int x = -ri; x <= ri; x++) for (int y = -ri; y <= ri; y++) for (int z = -ri; z <= ri; z++)
            {
                var c = c0 + new Vector3Int(x, y, z);
                if ((BlockWorld.Center(c) - at).sqrMagnitude > r * r * (0.7f + Random.value * 0.3f) || dug.Contains(c) || BlockWorld.Has(c)) continue;
                if (Fullness(c) == 0) continue;
                cells.Add(c);
            }
            if (cells.Count == 0) return;
            CutCells(cells);
            foreach (var c in cells) { dug.Add(c); partial.Remove(c); }
            foreach (var c in cells) Uncover(c);
            RebuildFaces();
            Persist.Dirty = true;
        }

        // ------------------------------------------------------------------------------------------------ cutting the scenery

        /// <summary>A scenery mesh we cut: our own copy, its vertices (local and world) and its live triangles.</summary>
        private class Cut
        {
            public Mesh Copy;
            public Matrix4x4 ToWorld;
            public List<Vector3> Pos = new List<Vector3>(), World = new List<Vector3>(), Nrm = new List<Vector3>();
            public List<Vector4> Tan = new List<Vector4>();
            public List<Vector2> Uv = new List<Vector2>(), Uv2 = new List<Vector2>();
            public List<Color> Col = new List<Color>();
            public List<int>[] Tris;
        }

        private static readonly Dictionary<int, Cut> cuts = new Dictionary<int, Cut>();
        private static readonly HashSet<int> unreadable = new HashSet<int>();

        /// <summary>Cuts these cells out of every scenery mesh (visible and collision) around them.</summary>
        private static void CutCells(List<Vector3Int> cells)
        {
            var bounds = new Bounds(BlockWorld.Center(cells[0]), Vector3.one);
            foreach (var c in cells) bounds.Encapsulate(new Bounds(BlockWorld.Center(c), Vector3.one));
            var cubes = new List<Bounds>();
            foreach (var c in cells) cubes.Add(new Bounds(BlockWorld.Center(c), Vector3.one));

            var done = new HashSet<int>();
            foreach (var col in Physics.OverlapBox(bounds.center, bounds.extents + Vector3.one * 0.05f, Quaternion.identity, SrWorld.SolidMask, QueryTriggerInteraction.Ignore))
            {
                if (Ours(col) || !SrWorld.IsTerrain(col)) continue;
                // the visible meshes of that piece of scenery
                var lod = col.GetComponentInParent<LODGroup>();
                var root = lod != null ? lod.transform : col.transform;
                foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
                {
                    var mr = mf.GetComponent<MeshRenderer>();
                    if (mr == null || !done.Add(mf.GetInstanceID()) || !mr.bounds.Intersects(bounds)) continue;
                    var f = mf;
                    CutMesh(f.GetInstanceID(), f.sharedMesh, f.transform, cubes, m => f.sharedMesh = m);
                }
                // its collision: a flat box (the ranch ground) becomes a mesh collider we can cut
                var mc = col as MeshCollider;
                if (col is BoxCollider box && Thin(box) && col.GetComponent<MeshFilter>() != null)
                {
                    mc = col.GetComponent<MeshCollider>() ?? col.gameObject.AddComponent<MeshCollider>();
                    if (!cuts.ContainsKey(mc.GetInstanceID())) mc.sharedMesh = col.GetComponent<MeshFilter>().sharedMesh;
                    box.enabled = false;
                }
                if (mc != null && done.Add(mc.GetInstanceID()))
                {
                    var m2 = mc;
                    CutMesh(m2.GetInstanceID(), m2.sharedMesh, m2.transform, cubes, m => { m2.sharedMesh = null; m2.sharedMesh = m; });
                }
            }
            Physics.SyncTransforms();
        }

        private static void CutMesh(int key, Mesh mesh, Transform t, List<Bounds> cubes, System.Action<Mesh> assign)
        {
            if (mesh == null || unreadable.Contains(key)) return;
            if (!cuts.TryGetValue(key, out var cut))
            {
                if (!mesh.isReadable) { unreadable.Add(key); Plugin.Log.LogWarning("scenery mesh " + mesh.name + " is not readable: it stays whole"); return; }
                cut = new Cut { Copy = Object.Instantiate(mesh), ToWorld = t.localToWorldMatrix };
                cut.Copy.name = mesh.name + " (dug)";
                mesh.GetVertices(cut.Pos); mesh.GetNormals(cut.Nrm); mesh.GetTangents(cut.Tan);
                mesh.GetUVs(0, cut.Uv); mesh.GetUVs(1, cut.Uv2); mesh.GetColors(cut.Col);
                foreach (var p in cut.Pos) cut.World.Add(cut.ToWorld.MultiplyPoint3x4(p));
                cut.Tris = new List<int>[mesh.subMeshCount];
                for (int s = 0; s < mesh.subMeshCount; s++) { cut.Tris[s] = new List<int>(); mesh.GetTriangles(cut.Tris[s], s); }
                cuts[key] = cut;
            }
            bool changed = false;
            foreach (var cube in cubes)
                for (int s = 0; s < cut.Tris.Length; s++)
                    if (Knife(cut, s, cube)) changed = true;
            if (!changed) { assign(cut.Copy); return; }
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
        }

        /// <summary>
        /// The six faces of the cube as knives: each cut keeps the piece outside and passes the piece inside on to the
        /// next knife; what is still inside after the sixth is thrown away. Kept pieces are fanned back into triangles.
        /// </summary>
        private static bool Knife(Cut cut, int sub, Bounds cube)
        {
            var tris = cut.Tris[sub];
            var outList = new List<int>(tris.Count);
            bool changed = false;
            var normals = new[] { Vector3.left, Vector3.right, Vector3.down, Vector3.up, Vector3.back, Vector3.forward };
            var dists = new[] { -cube.min.x, cube.max.x, -cube.min.y, cube.max.y, -cube.min.z, cube.max.z };
            for (int i = 0; i < tris.Count; i += 3)
            {
                int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                Vector3 wa = cut.World[a], wb = cut.World[b], wc = cut.World[c];
                var tb = new Bounds(wa, Vector3.zero); tb.Encapsulate(wb); tb.Encapsulate(wc);
                if (!tb.Intersects(cube)) { outList.Add(a); outList.Add(b); outList.Add(c); continue; }
                changed = true;
                var inside = new List<int> { a, b, c };
                for (int pl = 0; pl < 6 && inside.Count >= 3; pl++)
                {
                    // outside of this face: n.p > d
                    var n = normals[pl]; float d = dists[pl];
                    var front = new List<int>(); var back = new List<int>();
                    for (int k = 0; k < inside.Count; k++)
                    {
                        int p = inside[k], q = inside[(k + 1) % inside.Count];
                        float fp = Vector3.Dot(n, cut.World[p]) - d, fq = Vector3.Dot(n, cut.World[q]) - d;
                        if (fp > 0) front.Add(p); else back.Add(p);
                        if ((fp > 0) != (fq > 0))
                        {
                            int m = Lerp(cut, p, q, fp / (fp - fq));
                            front.Add(m); back.Add(m);
                        }
                    }
                    for (int k = 1; k + 1 < front.Count; k++) { outList.Add(front[0]); outList.Add(front[k]); outList.Add(front[k + 1]); }
                    inside = back;
                }
            }
            if (changed) cut.Tris[sub] = outList;
            return changed;
        }

        /// <summary>A new vertex part way along an edge, every attribute blended.</summary>
        private static int Lerp(Cut c, int a, int b, float t)
        {
            int n = c.Pos.Count;
            c.Pos.Add(Vector3.Lerp(c.Pos[a], c.Pos[b], t));
            c.World.Add(Vector3.Lerp(c.World[a], c.World[b], t));
            if (c.Nrm.Count > a) c.Nrm.Add(Vector3.Lerp(c.Nrm[a], c.Nrm[b], t).normalized);
            if (c.Tan.Count > a) c.Tan.Add(Vector4.Lerp(c.Tan[a], c.Tan[b], t));
            if (c.Uv.Count > a) c.Uv.Add(Vector2.Lerp(c.Uv[a], c.Uv[b], t));
            if (c.Uv2.Count > a) c.Uv2.Add(Vector2.Lerp(c.Uv2[a], c.Uv2[b], t));
            if (c.Col.Count > a) c.Col.Add(Color.Lerp(c.Col[a], c.Col[b], t));
            return n;
        }

        // ------------------------------------------------------------------------------------------------ trimmed faces

        private static GameObject faces;
        private static Mesh facesMesh;

        /// <summary>
        /// The walls of the hole in cells the surface goes through: each face that looks into a dug cell, cut where it
        /// runs into the original ground (only the part under the surface is drawn and solid).
        /// </summary>
        private static void RebuildFaces()
        {
            if (faces == null)
            {
                faces = new GameObject("SlimeCraft dug ground");
                faces.AddComponent<MeshFilter>();
                faces.AddComponent<MeshRenderer>().sharedMaterial = BlockWorld.Materials[0];
                faces.AddComponent<MeshCollider>();
                facesMesh = new Mesh { name = "dug ground" };
                facesMesh.MarkDynamic();
            }
            var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var col = new List<Color>(); var tri = new List<int>();
            foreach (var c in partial)
            {
                var block = GroundBlock(c);
                var rect = Atlas.Rect(Particles.ParticleTex(block.Default));
                for (int d = 0; d < 6; d++)
                    if (dug.Contains(c + BlockWorld.Offset((Dir)d))) Face(c, (Dir)d, rect, v, n, uv, col, tri);
            }
            facesMesh.Clear();
            if (v.Count > 65000) facesMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            facesMesh.SetVertices(v); facesMesh.SetNormals(n); facesMesh.SetUVs(0, uv); facesMesh.SetColors(col); facesMesh.SetTriangles(tri, 0);
            facesMesh.RecalculateBounds();
            faces.GetComponent<MeshFilter>().sharedMesh = facesMesh;
            var mc = faces.GetComponent<MeshCollider>();
            mc.sharedMesh = null;
            if (tri.Count > 0) mc.sharedMesh = facesMesh;
        }

        /// <summary>One face of a cell, clipped to the ground under the original surface (Depth > 0 at its corners).</summary>
        private static void Face(Vector3Int c, Dir d, Rect rect, List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<Color> col, List<int> tri)
        {
            Vector3 o = BlockWorld.Offset(d);
            // two axes along the face; for walls the second one is up, so the texture stands upright
            Vector3 a, b;
            if (d == Dir.Up || d == Dir.Down) { a = Vector3.right; b = Vector3.forward; }
            else { a = Vector3.Cross(Vector3.up, o).normalized; b = Vector3.up; }
            var center = BlockWorld.Center(c) + o * 0.5f;
            var origin = center - a * 0.5f - b * 0.5f;
            var q = new[] { origin, origin + a, origin + a + b, origin + b };
            var f = new float[4];
            // sampled a hair inside the cell, so the dug neighbour's emptiness doesn't count
            for (int i = 0; i < 4; i++) f[i] = Depth(q[i] - o * 0.002f + (center - q[i]) * 0.002f);
            ClipTri(q[0], q[1], q[2], f[0], f[1], f[2], o, origin, a, b, rect, v, n, uv, col, tri);
            ClipTri(q[0], q[2], q[3], f[0], f[2], f[3], o, origin, a, b, rect, v, n, uv, col, tri);
        }

        private static void ClipTri(Vector3 p0, Vector3 p1, Vector3 p2, float f0, float f1, float f2, Vector3 normal, Vector3 origin, Vector3 a, Vector3 b,
                                    Rect rect, List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<Color> col, List<int> tri)
        {
            var pts = new[] { p0, p1, p2 };
            var fs = new[] { f0, f1, f2 };
            var poly = new List<Vector3>();
            for (int i = 0; i < 3; i++)
            {
                int j = (i + 1) % 3;
                if (fs[i] > 0) poly.Add(pts[i]);
                if ((fs[i] > 0) != (fs[j] > 0)) poly.Add(Vector3.Lerp(pts[i], pts[j], fs[i] / (fs[i] - fs[j])));
            }
            if (poly.Count < 3) return;
            int start = v.Count;
            foreach (var p in poly)
            {
                v.Add(p); n.Add(normal); col.Add(Color.white);
                float u = Vector3.Dot(p - origin, a), w = Vector3.Dot(p - origin, b);
                uv.Add(new Vector2(rect.x + rect.width * Mathf.Clamp01(u), rect.y + rect.height * Mathf.Clamp01(w)));
            }
            for (int k = 1; k + 1 < poly.Count; k++)
            {
                int i0 = start, i1 = start + k, i2 = start + k + 1;
                // facing out of the ground, into the dug cell
                if (Vector3.Dot(Vector3.Cross(v[i1] - v[i0], v[i2] - v[i0]), normal) < 0) { tri.Add(i0); tri.Add(i2); tri.Add(i1); }
                else { tri.Add(i0); tri.Add(i1); tri.Add(i2); }
            }
        }

        // ------------------------------------------------------------------------------------------------ blocks

        /// <summary>Minecraft's layers under each kind of ground: a top block, a few of the soil, then stone with ores.</summary>
        private static BlockDef BlockFor(string kind, float depth, Vector3Int cell)
        {
            string id;
            switch (kind)
            {
                case "sand":
                    id = depth < 3f ? "sand" : depth < 5f ? "sandstone" : null; break;
                case "grass": case "mushroom":
                    id = depth < 0f ? (kind == "mushroom" ? "mycelium" : "grass_block") : depth < 3f ? "dirt" : null; break;
                case "dirt":
                    // the Far Far Range's red earth
                    id = depth < 0f ? "red_sand" : depth < 3f ? (Hash(cell) < 0.2f ? "orange_terracotta" : "terracotta") : null; break;
                case "wood": case "log":
                    id = "oak_planks"; break;
                case "metal":
                    id = "iron_block"; break;
                case "glass":
                    id = "glass"; break;
                default:
                    id = null; break;
            }
            if (id != null) return McData.Block(id);
            var ore = Ore(depth, cell);
            if (ore != null && McData.Block(ore) != null) return McData.Block(ore);
            if (depth > 40f) return McData.Block("deepslate");
            float nz = Mathf.PerlinNoise(cell.x * 0.11f + cell.y * 0.07f, cell.z * 0.11f - cell.y * 0.05f);
            return McData.Block(nz < 0.18f ? "granite" : nz > 0.84f ? "diorite" : nz > 0.74f ? "andesite" : "stone");
        }

        /// <summary>A fixed random number per cell, so a cell is always the same block.</summary>
        private static float Hash(Vector3Int c)
        {
            uint h = (uint)(c.x * 73856093 ^ c.y * 19349663 ^ c.z * 83492791);
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return (h & 0xffffff) / (float)0x1000000;
        }

        private static string Ore(float depth, Vector3Int cell)
        {
            float r = Hash(cell), acc = 0;
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

        // ------------------------------------------------------------------------------------------------ save

        public static void Clear()
        {
            surface.Clear(); registered.Clear(); dug.Clear(); partial.Clear(); kinds.Clear(); cuts.Clear(); unreadable.Clear();
            if (faces != null) Object.Destroy(faces);
            faces = null;
        }

        public static JObject Save()
        {
            var d = new JArray();
            foreach (var c in dug) d.Add(new JArray(c.x, c.y, c.z));
            var p = new JArray();
            foreach (var c in partial) p.Add(new JArray(c.x, c.y, c.z));
            return new JObject { ["dug"] = d, ["partial"] = p };
        }

        /// <summary>Back in a game: the blocks come back with the world, the scenery gets its holes cut again.</summary>
        public static void Load(JObject o)
        {
            Clear();
            if (o == null || !(o["dug"] is JArray d) || d.Count == 0) return;
            var cells = new List<Vector3Int>();
            foreach (JArray a in d) cells.Add(new Vector3Int((int)a[0], (int)a[1], (int)a[2]));
            foreach (var c in cells) RegisterNear(BlockWorld.Center(c), 3f);
            CutCells(cells);
            foreach (var c in cells) dug.Add(c);
            if (o["partial"] is JArray p)
                foreach (JArray a in p)
                {
                    var c = new Vector3Int((int)a[0], (int)a[1], (int)a[2]);
                    partial.Add(c);
                    Depth(BlockWorld.Center(c), out var k);
                    if (k != null) kinds[c] = k;
                }
            RebuildFaces();
        }
    }
}
