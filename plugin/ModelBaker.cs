using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SlimeCraft
{
    public enum Dir { Down, Up, North, South, West, East, None }

    public class Quad
    {
        public Vector3[] Pos = new Vector3[4];   // Unity block space, 0..1
        public Vector2[] Uv = new Vector2[4];    // atlas UV
        public Vector3 Normal;
        public Dir Cull = Dir.None;              // face is hidden when the neighbour on this side is a full opaque cube
        public bool Shade = true;
        public string Tex;
    }

    /// <summary>
    /// Minecraft's FaceBakery and ModelRotation: turns a block model's elements (from/to boxes with per-face
    /// UVs, face rotations, element rotations with rescale) plus the blockstate's x/y rotation and uvlock into
    /// quads. All maths happens in Minecraft's own right-handed space (x east, y up, z south, 0..16) and only the
    /// final vertices are mirrored into Unity's left-handed space (z flipped), with the winding reversed.
    /// </summary>
    public static class ModelBaker
    {
        private static readonly Dictionary<string, List<Quad>> cache = new Dictionary<string, List<Quad>>();

        public static Vector3 McToUnity(Vector3 p) => new Vector3(p.x, p.y, 1f - p.z);
        public static Vector3 McDirToUnity(Vector3 d) => new Vector3(d.x, d.y, -d.z);

        public static readonly Vector3[] Normals =
        {
            Vector3.down, Vector3.up, new Vector3(0, 0, -1), new Vector3(0, 0, 1), Vector3.left, Vector3.right,
        };

        public static Dir ParseDir(string s)
        {
            switch (s)
            {
                case "down": return Dir.Down;
                case "up": return Dir.Up;
                case "north": return Dir.North;
                case "south": return Dir.South;
                case "west": return Dir.West;
                case "east": return Dir.East;
                default: return Dir.None;
            }
        }

        /// <summary>Direction in MC space after the blockstate rotation.</summary>
        public static Dir RotateDir(Dir d, int x, int y)
        {
            if (d == Dir.None) return d;
            var v = RotX(Normals[(int)d], x);
            v = RotY(v, y);
            for (int i = 0; i < 6; i++) if (Vector3.Dot(Normals[i], v) > 0.9f) return (Dir)i;
            return d;
        }

        // ModelRotation: -x degrees about +X, then -y degrees about +Y (right-handed), around the block centre.
        private static Vector3 RotX(Vector3 v, int deg)
        {
            float a = -deg * Mathf.Deg2Rad, c = Mathf.Round(Mathf.Cos(a)), s = Mathf.Round(Mathf.Sin(a));
            return new Vector3(v.x, v.y * c - v.z * s, v.y * s + v.z * c);
        }

        private static Vector3 RotY(Vector3 v, int deg)
        {
            float a = -deg * Mathf.Deg2Rad, c = Mathf.Round(Mathf.Cos(a)), s = Mathf.Round(Mathf.Sin(a));
            return new Vector3(v.x * c + v.z * s, v.y, -v.x * s + v.z * c);
        }

        public static List<Quad> Bake(ModelRef r, int tint = -1)
        {
            var key = r.Model + "|" + r.X + "|" + r.Y + "|" + r.UvLock + "|" + tint;
            if (cache.TryGetValue(key, out var q)) return q;
            q = new List<Quad>();
            if (McData.Models.TryGetValue(r.Model, out var m) && m["elements"] is JArray els)
                foreach (JObject e in els) BakeElement(e, r, tint, q);
            cache[key] = q;
            return q;
        }

        public static List<Quad> Bake(string model, int tint = -1) => Bake(new ModelRef { Model = model }, tint);

        private static Vector3 V(JToken a) => new Vector3((float)a[0], (float)a[1], (float)a[2]);

        private static void BakeElement(JObject e, ModelRef r, int tint, List<Quad> outQ)
        {
            var from = V(e["from"]);
            var to = V(e["to"]);
            bool shade = e["shade"] == null || (bool)e["shade"];

            // element rotation
            Vector3 origin = Vector3.one * 8; Vector3 axis = Vector3.zero; float angle = 0; bool rescale = false;
            if (e["rotation"] is JObject rot)
            {
                origin = V(rot["origin"]);
                angle = (float)rot["angle"];
                rescale = rot["rescale"] != null && (bool)rot["rescale"];
                switch ((string)rot["axis"]) { case "x": axis = Vector3.right; break; case "y": axis = Vector3.up; break; default: axis = Vector3.forward; break; }
            }

            foreach (var f in (JObject)e["faces"])
            {
                var dir = ParseDir(f.Key);
                if (dir == Dir.None) continue;
                var face = (JObject)f.Value;
                var tex = (string)face["tex"];
                if (face["tint"] != null && tint >= 0) tex = Atlas.Tinted(tex, tint);

                // corners in MC space, in MC's vertex order for the face (top-left, bottom-left, bottom-right, top-right as seen from outside)
                var c = Corners(dir, from, to);
                // default UV of each corner from the element bounds (Minecraft's BlockElementFace defaults)
                float[] uv;
                if (face["uv"] is JArray ua) uv = new[] { (float)ua[0], (float)ua[1], (float)ua[2], (float)ua[3] };
                else uv = DefaultUv(dir, from, to);
                int frot = face["rot"] != null ? (int)face["rot"] : 0;

                // uvlock: the texture stays fixed to the world while the model turns; recompute UVs from the
                // rotated geometry for the faces Minecraft locks (its uvlock maths reduces to this for 90 degree turns).
                var q = new Quad { Shade = shade };
                var rect = Atlas.Rect(tex);
                q.Tex = tex;
                for (int i = 0; i < 4; i++)
                {
                    var p = c[i];
                    if (angle != 0) p = RotateElement(p, origin, axis, angle, rescale);
                    // blockstate rotation about the centre
                    var pc = (p - Vector3.one * 8) ;
                    pc = RotY(RotX(pc, r.X), r.Y);
                    p = pc + Vector3.one * 8;
                    q.Pos[i] = McToUnity(p / 16f);
                }
                var faceUv = CornerUvs(uv, frot);
                if (r.UvLock && (r.X != 0 || r.Y != 0))
                {
                    var nd = RotateDir(dir, r.X, r.Y);
                    var locked = LockedUvs(q.Pos, nd);
                    if (locked != null) faceUv = locked;
                }
                for (int i = 0; i < 4; i++)
                {
                    var u = faceUv[i];
                    // inset a hair so point sampling never bleeds into the neighbouring atlas slot
                    float ux = Mathf.Clamp(u.x / 16f, 0.0005f, 0.9995f), uy = Mathf.Clamp(u.y / 16f, 0.0005f, 0.9995f);
                    q.Uv[i] = new Vector2(rect.x + ux * rect.width, rect.y + (1f - uy) * rect.height);
                }
                var nrm = Normals[(int)dir];
                if (angle != 0) nrm = Quaternion.AngleAxis(-angle, axis) * nrm; // MC rotates counter-clockwise for positive angles (right-handed)
                nrm = RotY(RotX(nrm, r.X), r.Y);
                q.Normal = McDirToUnity(nrm).normalized;
                q.Cull = face["cull"] != null ? RotateDir(ParseDir((string)face["cull"]), r.X, r.Y) : Dir.None;
                // Mirroring z flips the winding: reverse the quad so it faces outwards in Unity.
                Swap(q.Pos, 1, 3);
                Swap(q.Uv, 1, 3);
                outQ.Add(q);
            }
        }

        private static void Swap<T>(T[] a, int i, int j) { var t = a[i]; a[i] = a[j]; a[j] = t; }

        private static Vector3 RotateElement(Vector3 p, Vector3 origin, Vector3 axis, float angle, bool rescale)
        {
            // Right-handed rotation by `angle` about the axis; Unity's Quaternion is left-handed, hence the minus.
            var d = p - origin;
            d = Quaternion.AngleAxis(-angle, axis) * d;
            if (rescale)
            {
                float k = 1f / Mathf.Cos(Mathf.Abs(angle) * Mathf.Deg2Rad);
                var s = new Vector3(axis.x != 0 ? 1 : k, axis.y != 0 ? 1 : k, axis.z != 0 ? 1 : k);
                d = Vector3.Scale(d, s);
            }
            return d + origin;
        }

        /// <summary>
        /// The four corners of a face in MC space, ordered so that corner 0 takes UV (u1,v1), 1 takes (u1,v2),
        /// 2 takes (u2,v2) and 3 takes (u2,v1) - Minecraft's vertex order for each direction (CubeFace).
        /// </summary>
        private static Vector3[] Corners(Dir d, Vector3 a, Vector3 b)
        {
            switch (d)
            {
                case Dir.Down: return new[] { new Vector3(a.x, a.y, b.z), new Vector3(a.x, a.y, a.z), new Vector3(b.x, a.y, a.z), new Vector3(b.x, a.y, b.z) };
                case Dir.Up: return new[] { new Vector3(a.x, b.y, a.z), new Vector3(a.x, b.y, b.z), new Vector3(b.x, b.y, b.z), new Vector3(b.x, b.y, a.z) };
                case Dir.North: return new[] { new Vector3(b.x, b.y, a.z), new Vector3(b.x, a.y, a.z), new Vector3(a.x, a.y, a.z), new Vector3(a.x, b.y, a.z) };
                case Dir.South: return new[] { new Vector3(a.x, b.y, b.z), new Vector3(a.x, a.y, b.z), new Vector3(b.x, a.y, b.z), new Vector3(b.x, b.y, b.z) };
                case Dir.West: return new[] { new Vector3(a.x, b.y, a.z), new Vector3(a.x, a.y, a.z), new Vector3(a.x, a.y, b.z), new Vector3(a.x, b.y, b.z) };
                default: return new[] { new Vector3(b.x, b.y, b.z), new Vector3(b.x, a.y, b.z), new Vector3(b.x, a.y, a.z), new Vector3(b.x, b.y, a.z) };
            }
        }

        /// <summary>Minecraft's default face UVs when a model leaves "uv" out (BlockElement.getRotatedMatrix defaults).</summary>
        private static float[] DefaultUv(Dir d, Vector3 a, Vector3 b)
        {
            switch (d)
            {
                case Dir.Down: return new[] { a.x, 16 - b.z, b.x, 16 - a.z };
                case Dir.Up: return new[] { a.x, a.z, b.x, b.z };
                case Dir.North: return new[] { 16 - b.x, 16 - b.y, 16 - a.x, 16 - a.y };
                case Dir.South: return new[] { a.x, 16 - b.y, b.x, 16 - a.y };
                case Dir.West: return new[] { a.z, 16 - b.y, b.z, 16 - a.y };
                default: return new[] { 16 - b.z, 16 - b.y, 16 - a.z, 16 - a.y };
            }
        }

        /// <summary>UV of corners 0..3 for uv rect [u1,v1,u2,v2] turned by the face's rotation (clockwise, like Minecraft).</summary>
        private static Vector2[] CornerUvs(float[] uv, int rot)
        {
            var c = new[] { new Vector2(uv[0], uv[1]), new Vector2(uv[0], uv[3]), new Vector2(uv[2], uv[3]), new Vector2(uv[2], uv[1]) };
            int s = ((rot / 90) % 4 + 4) % 4;
            var r = new Vector2[4];
            for (int i = 0; i < 4; i++) r[i] = c[(i + s) % 4];
            return r;
        }

        /// <summary>World-locked UVs for a face after rotation, from the Unity-space positions (uvlock).</summary>
        private static Vector2[] LockedUvs(Vector3[] pos, Dir d)
        {
            var r = new Vector2[4];
            for (int i = 0; i < 4; i++)
            {
                // back to MC space for the projection
                var p = new Vector3(pos[i].x, pos[i].y, 1f - pos[i].z) * 16f;
                switch (d)
                {
                    case Dir.Up: r[i] = new Vector2(p.x, p.z); break;
                    case Dir.Down: r[i] = new Vector2(p.x, 16 - p.z); break;
                    case Dir.North: r[i] = new Vector2(16 - p.x, 16 - p.y); break;
                    case Dir.South: r[i] = new Vector2(p.x, 16 - p.y); break;
                    case Dir.West: r[i] = new Vector2(p.z, 16 - p.y); break;
                    case Dir.East: r[i] = new Vector2(16 - p.z, 16 - p.y); break;
                    default: return null;
                }
            }
            return r;
        }

        // ------------------------------------------------------------------------------------------------ meshes

        /// <summary>A standalone mesh of a whole model (centred on the block centre), for items in hand, icons and drops.</summary>
        public static Mesh ModelMesh(string model, int tint, out bool translucent)
        {
            translucent = false;
            var quads = Bake(model, tint);
            var mb = new MeshBuilder();
            foreach (var q in quads) mb.Add(q, -Vector3.one * 0.5f);
            return mb.Build(model);
        }

        /// <summary>Minecraft's item/generated model: each layer texture extruded one pixel thick, sides only where pixels are opaque.</summary>
        public static Mesh GeneratedMesh(List<string> layers, Color[] layerTints)
        {
            var mb = new MeshBuilder();
            for (int li = 0; li < layers.Count; li++)
            {
                var src = Atlas.Source(layers[li]);
                if (src == null) continue;
                var rect = Atlas.Rect(layers[li]);
                var tint = layerTints != null && li < layerTints.Length ? layerTints[li] : Color.white;
                int w = src.width, h = src.width; // first frame only
                var px = src.GetPixels32();
                bool Op(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && px[y * w + x].a > 0;
                float z0 = 7.5f / 16f - 0.5f, z1 = 8.5f / 16f - 0.5f;
                // Each layer sits a hair in front of the previous one so overlays (potions, leather) never z-fight.
                float off = li * 0.001f;
                Vector3 P(float x, float y, float z) => new Vector3(x / w - 0.5f, y / h - 0.5f, z - off);
                Vector2 U(float x, float y) => new Vector2(rect.x + x / w * rect.width, rect.y + y / h * rect.height);
                mb.Raw(P(0, 0, z0), P(0, h, z0), P(w, h, z0), P(w, 0, z0), Vector3.back, U(0, 0), U(0, h), U(w, h), U(w, 0), tint);
                mb.Raw(P(w, 0, z1), P(w, h, z1), P(0, h, z1), P(0, 0, z1), Vector3.forward, U(w, 0), U(w, h), U(0, h), U(0, 0), tint);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        if (!Op(x, y)) continue;
                        var c = U(x + 0.5f, y + 0.5f);
                        if (!Op(x - 1, y)) mb.Raw(P(x, y, z1), P(x, y + 1, z1), P(x, y + 1, z0), P(x, y, z0), Vector3.left, c, c, c, c, tint);
                        if (!Op(x + 1, y)) mb.Raw(P(x + 1, y, z0), P(x + 1, y + 1, z0), P(x + 1, y + 1, z1), P(x + 1, y, z1), Vector3.right, c, c, c, c, tint);
                        if (!Op(x, y - 1)) mb.Raw(P(x, y, z0), P(x + 1, y, z0), P(x + 1, y, z1), P(x, y, z1), Vector3.down, c, c, c, c, tint);
                        if (!Op(x, y + 1)) mb.Raw(P(x, y + 1, z1), P(x + 1, y + 1, z1), P(x + 1, y + 1, z0), P(x, y + 1, z0), Vector3.up, c, c, c, c, tint);
                    }
            }
            return mb.Build("generated");
        }
    }

    /// <summary>Accumulates quads into a mesh (one submesh per render layer when needed).</summary>
    public class MeshBuilder
    {
        private readonly List<Vector3> v = new List<Vector3>();
        private readonly List<Vector3> n = new List<Vector3>();
        private readonly List<Vector2> uv = new List<Vector2>();
        private readonly List<Color> col = new List<Color>();
        private readonly List<int>[] tris;

        public MeshBuilder(int submeshes = 1)
        {
            tris = new List<int>[submeshes];
            for (int i = 0; i < submeshes; i++) tris[i] = new List<int>();
        }

        public int Count => v.Count;

        public void Add(Quad q, Vector3 offset, int sub = 0) => Add(q, offset, sub, Color.white);

        public void Add(Quad q, Vector3 offset, int sub, Color c)
        {
            int b = v.Count;
            for (int i = 0; i < 4; i++) { v.Add(q.Pos[i] + offset); n.Add(q.Normal); uv.Add(q.Uv[i]); col.Add(c); }
            var t = tris[sub];
            t.Add(b); t.Add(b + 1); t.Add(b + 2);
            t.Add(b); t.Add(b + 2); t.Add(b + 3);
        }

        /// <summary>A quad given clockwise (Unity front face) as seen from outside.</summary>
        public void Raw(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud, Color color, int sub = 0)
        {
            int i = v.Count;
            v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            n.Add(normal); n.Add(normal); n.Add(normal); n.Add(normal);
            uv.Add(ua); uv.Add(ub); uv.Add(uc); uv.Add(ud);
            col.Add(color); col.Add(color); col.Add(color); col.Add(color);
            var t = tris[sub];
            t.Add(i); t.Add(i + 1); t.Add(i + 2);
            t.Add(i); t.Add(i + 2); t.Add(i + 3);
        }

        public Mesh Build(string name)
        {
            var m = new Mesh { name = name };
            if (v.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(v);
            m.SetNormals(n);
            m.SetUVs(0, uv);
            m.SetColors(col);
            m.subMeshCount = tris.Length;
            for (int i = 0; i < tris.Length; i++) m.SetTriangles(tris[i], i);
            m.RecalculateBounds();
            m.RecalculateTangents();
            return m;
        }
    }
}
