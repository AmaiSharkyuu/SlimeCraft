using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Minecraft's ModelPart, ported as is: cuboids with box UVs (the exact vertex and UV order of
    /// ModelPart.Cuboid), pivots in pixels, rotations as rotationZYX(roll, yaw, pitch), children.
    ///
    /// Model space in Minecraft is y-down and rendered through scale(-1,-1,1) + translate(0,-1.501,0); combined
    /// with the mirror into Unity's left-handed world (z flipped) that is just v -> -v, a point reflection, which
    /// commutes with every rotation. So each part keeps Minecraft's rotation numbers untouched, positions are
    /// negated, the root sits 1.501 blocks up, and the reflection turns Minecraft's counter-clockwise quads into
    /// Unity's clockwise front faces without reordering anything.
    /// </summary>
    public class MPart
    {
        public string Name;
        public Vector3 Pivot;              // pixels
        public float Pitch, Yaw, Roll;     // radians, like ModelTransform
        public readonly List<Cuboid> Cuboids = new List<Cuboid>();
        public readonly List<MPart> Children = new List<MPart>();
        public Transform T;                // built object

        public struct Cuboid
        {
            public int U, V;
            public Vector3 Pos, Size;
            public Vector3 Extra;
            public bool Mirror;
            public HashSet<Dir> Faces;
        }

        public MPart(string name) { Name = name; }

        public MPart Box(int u, int v, float x, float y, float z, float sx, float sy, float sz, float dilation = 0, bool mirror = false)
        {
            Cuboids.Add(new Cuboid { U = u, V = v, Pos = new Vector3(x, y, z), Size = new Vector3(sx, sy, sz), Extra = Vector3.one * dilation, Mirror = mirror });
            return this;
        }

        public MPart BoxFaces(int u, int v, float x, float y, float z, float sx, float sy, float sz, params Dir[] faces)
        {
            Cuboids.Add(new Cuboid { U = u, V = v, Pos = new Vector3(x, y, z), Size = new Vector3(sx, sy, sz), Faces = new HashSet<Dir>(faces) });
            return this;
        }

        public MPart At(float px, float py, float pz, float pitch = 0, float yaw = 0, float roll = 0)
        {
            Pivot = new Vector3(px, py, pz); Pitch = pitch; Yaw = yaw; Roll = roll;
            return this;
        }

        public MPart Add(MPart c) { Children.Add(c); return this; }

        public MPart Find(string name)
        {
            if (Name == name) return this;
            foreach (var c in Children) { var f = c.Find(name); if (f != null) return f; }
            return null;
        }

        public static Quaternion Rot(float pitch, float yaw, float roll) =>
            Quaternion.AngleAxis(roll * Mathf.Rad2Deg, Vector3.forward) * Quaternion.AngleAxis(yaw * Mathf.Rad2Deg, Vector3.up) * Quaternion.AngleAxis(pitch * Mathf.Rad2Deg, Vector3.right);

        /// <summary>Sets the part's pose the way Minecraft model code does (pivot + angles), in place.</summary>
        public void Pose(float pitch, float yaw, float roll)
        {
            Pitch = pitch; Yaw = yaw; Roll = roll;
            if (T != null) T.localRotation = Rot(pitch, yaw, roll);
        }

        public void PoseAt(Vector3 pivot, float pitch, float yaw, float roll)
        {
            Pivot = pivot;
            if (T != null) T.localPosition = -pivot / 16f;
            Pose(pitch, yaw, roll);
        }

        public void SetVisible(bool v) { if (T != null) T.gameObject.SetActive(v); }

        /// <summary>Builds the part (and children) under parent as Unity objects with meshes over `mat`.</summary>
        public Transform Build(Transform parent, Material mat, float texW, float texH, int layer)
        {
            var go = new GameObject(Name) { layer = layer };
            T = go.transform;
            T.SetParent(parent, false);
            T.localPosition = -Pivot / 16f;
            T.localRotation = Rot(Pitch, Yaw, Roll);
            if (Cuboids.Count > 0)
            {
                var mesh = new GameObject("mesh") { layer = layer };
                mesh.transform.SetParent(T, false);
                mesh.AddComponent<MeshFilter>().sharedMesh = MakeMesh(texW, texH);
                var mr = mesh.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
            }
            foreach (var c in Children) c.Build(T, mat, texW, texH, layer);
            return T;
        }

        private Mesh MakeMesh(float tw, float th)
        {
            var mb = new MeshBuilder();
            foreach (var c in Cuboids) AddCuboid(mb, c, tw, th);
            return mb.Build(Name);
        }

        private struct Vtx { public Vector3 P; public float U, V; public Vtx(Vector3 p) { P = p; U = V = 0; } }

        private static void AddCuboid(MeshBuilder mb, Cuboid cb, float tw, float th)
        {
            float x = cb.Pos.x, y = cb.Pos.y, z = cb.Pos.z;
            float f = x + cb.Size.x, g = y + cb.Size.y, h = z + cb.Size.z;
            x -= cb.Extra.x; y -= cb.Extra.y; z -= cb.Extra.z;
            f += cb.Extra.x; g += cb.Extra.y; h += cb.Extra.z;
            if (cb.Mirror) { var t = f; f = x; x = t; }
            var v1 = new Vector3(x, y, z); var v2 = new Vector3(f, y, z); var v3 = new Vector3(f, g, z); var v4 = new Vector3(x, g, z);
            var v5 = new Vector3(x, y, h); var v6 = new Vector3(f, y, h); var v7 = new Vector3(f, g, h); var v8 = new Vector3(x, g, h);
            float sx = cb.Size.x, sy = cb.Size.y, sz = cb.Size.z;
            float j = cb.U, k = cb.U + sz, l = cb.U + sz + sx, m = cb.U + sz + sx + sx, n = cb.U + sz + sx + sz, o = cb.U + sz + sx + sz + sx;
            float p = cb.V, q = cb.V + sz, r = cb.V + sz + sy;
            void Q(Dir d, Vector3 a, Vector3 b, Vector3 c, Vector3 e, float u1, float vv1, float u2, float vv2)
            {
                if (cb.Faces != null && !cb.Faces.Contains(d)) return;
                var pos = new[] { a, b, c, e };
                var uv = new[] { new Vector2(u2, vv1), new Vector2(u1, vv1), new Vector2(u1, vv2), new Vector2(u2, vv2) };
                var nrm = ModelBaker.Normals[(int)d];
                if (cb.Mirror) { System.Array.Reverse(pos); System.Array.Reverse(uv); nrm.x = -nrm.x; }
                // model space -> Unity: v -> -v (see class comment), pixels -> blocks, texture v flipped for Unity
                Vector3 W(Vector3 v0) => -v0 / 16f;
                Vector2 T(Vector2 t) => new Vector2(t.x / tw, 1f - t.y / th);
                // the point reflection flips the winding: emit the quad reversed so it faces outwards
                mb.Raw(W(pos[0]), W(pos[3]), W(pos[2]), W(pos[1]), -nrm, T(uv[0]), T(uv[3]), T(uv[2]), T(uv[1]), Color.white);
            }
            Q(Dir.Down, v6, v5, v1, v2, k, p, l, q);
            Q(Dir.Up, v3, v4, v8, v7, l, q, m, p);
            Q(Dir.West, v1, v5, v8, v4, j, q, k, r);
            Q(Dir.North, v2, v1, v4, v3, k, q, l, r);
            Q(Dir.East, v6, v2, v3, v7, l, q, n, r);
            Q(Dir.South, v5, v6, v7, v8, n, q, o, r);
        }
    }

    /// <summary>A built entity model: the root part plus its Unity object.</summary>
    public class EntityModelInstance
    {
        public MPart Root;
        public GameObject Go;
        public Material Mat;

        public static EntityModelInstance Create(MPart root, Texture2D tex, Transform parent, int layer, bool cutout = true, float texW = 64, float texH = 64)
        {
            var e = new EntityModelInstance { Root = root };
            e.Go = new GameObject(root.Name + " model") { layer = layer };
            e.Go.transform.SetParent(parent, false);
            // Minecraft's living entity offset: the model's y = 24 px plane is the ground.
            var inner = new GameObject("offset") { layer = layer };
            inner.transform.SetParent(e.Go.transform, false);
            inner.transform.localPosition = new Vector3(0, 1.501f, 0);
            e.Mat = BlockWorld.MakeMat(cutout ? 1 : 0, tex);
            root.Build(inner.transform, e.Mat, texW, texH, layer);
            return e;
        }

        public MPart this[string name] => Root.Find(name);
    }

    /// <summary>Minecraft's model definitions (getTexturedModelData) that SubnautiCraft draws, ported from the decompiled code.</summary>
    public static class Models
    {
        /// <summary>PlayerEntityModel(slim) with its outer layers (hat, jacket, sleeves, pants).</summary>
        public static MPart Player(bool slim)
        {
            var root = new MPart("root");
            root.Add(new MPart("head").Box(0, 0, -4, -8, -4, 8, 8, 8).At(0, 0, 0)
                .Add(new MPart("hat").Box(32, 0, -4, -8, -4, 8, 8, 8, 0.5f)));
            root.Add(new MPart("body").Box(16, 16, -4, 0, -2, 8, 12, 4).At(0, 0, 0)
                .Add(new MPart("jacket").Box(16, 32, -4, 0, -2, 8, 12, 4, 0.25f)));
            if (slim)
            {
                root.Add(new MPart("left_arm").Box(32, 48, -1, -2, -2, 3, 12, 4).At(5, 2.5f, 0)
                    .Add(new MPart("left_sleeve").Box(48, 48, -1, -2, -2, 3, 12, 4, 0.25f)));
                root.Add(new MPart("right_arm").Box(40, 16, -2, -2, -2, 3, 12, 4).At(-5, 2.5f, 0)
                    .Add(new MPart("right_sleeve").Box(40, 32, -2, -2, -2, 3, 12, 4, 0.25f)));
            }
            else
            {
                root.Add(new MPart("left_arm").Box(32, 48, -1, -2, -2, 4, 12, 4).At(5, 2, 0)
                    .Add(new MPart("left_sleeve").Box(48, 48, -1, -2, -2, 4, 12, 4, 0.25f)));
                root.Add(new MPart("right_arm").Box(40, 16, -3, -2, -2, 4, 12, 4).At(-5, 2, 0)
                    .Add(new MPart("right_sleeve").Box(40, 32, -3, -2, -2, 4, 12, 4, 0.25f)));
            }
            root.Add(new MPart("left_leg").Box(16, 48, -2, 0, -2, 4, 12, 4).At(1.9f, 12, 0)
                .Add(new MPart("left_pants").Box(0, 48, -2, 0, -2, 4, 12, 4, 0.25f)));
            root.Add(new MPart("right_leg").Box(0, 16, -2, 0, -2, 4, 12, 4).At(-1.9f, 12, 0)
                .Add(new MPart("right_pants").Box(0, 32, -2, 0, -2, 4, 12, 4, 0.25f)));
            return root;
        }

        /// <summary>ElytraEntityModel (64x32 texture), drawn on the body 2 px behind it.</summary>
        public static MPart Elytra()
        {
            var root = new MPart("elytra");
            root.Add(new MPart("left_wing").Box(22, 0, -10, 0, 0, 10, 20, 2, 1f).At(5, 0, 0, Mathf.PI / 12f, 0, -Mathf.PI / 12f));
            root.Add(new MPart("right_wing").Box(22, 0, 0, 0, 0, 10, 20, 2, 1f, true).At(-5, 0, 0, Mathf.PI / 12f, 0, Mathf.PI / 12f));
            return root;
        }

        /// <summary>The first-person arm: PlayerEntityModel's right arm alone, at its pivot.</summary>
        public static MPart RightArm(bool slim)
        {
            var arm = new MPart("right_arm");
            if (slim) arm.Box(40, 16, -2, -2, -2, 3, 12, 4).Add(new MPart("right_sleeve").Box(40, 32, -2, -2, -2, 3, 12, 4, 0.25f));
            else arm.Box(40, 16, -3, -2, -2, 4, 12, 4).Add(new MPart("right_sleeve").Box(40, 32, -3, -2, -2, 4, 12, 4, 0.25f));
            return arm;
        }

        /// <summary>ChestBlockEntityRenderer.getSingleTexturedModelData (64x64).</summary>
        public static MPart Chest()
        {
            var root = new MPart("chest");
            root.Add(new MPart("bottom").Box(0, 19, 1, 0, 1, 14, 10, 14));
            root.Add(new MPart("lid").Box(0, 0, 1, 0, 0, 14, 5, 14).At(0, 9, 1));
            root.Add(new MPart("lock").Box(0, 0, 7, -2, 14, 2, 4, 1).At(0, 8, 0));
            return root;
        }

        /// <summary>ShulkerEntityModel base and lid (64x64).</summary>
        public static MPart Shulker()
        {
            var root = new MPart("shulker");
            root.Add(new MPart("lid").Box(0, 0, -8, -16, -8, 16, 12, 16).At(0, 24, 0));
            root.Add(new MPart("base").Box(0, 28, -8, -8, -8, 16, 8, 16).At(0, 24, 0));
            return root;
        }

        /// <summary>BedBlockEntityRenderer head and foot (64x64).</summary>
        public static MPart BedHead()
        {
            var root = new MPart("bed_head");
            root.Add(new MPart("main").Box(0, 0, 0, 0, 0, 16, 16, 6));
            root.Add(new MPart("left_leg").Box(50, 6, 0, 6, 0, 3, 3, 3).At(0, 0, 0, Mathf.PI / 2, 0, Mathf.PI / 2));
            root.Add(new MPart("right_leg").Box(50, 18, -16, 6, 0, 3, 3, 3).At(0, 0, 0, Mathf.PI / 2, 0, Mathf.PI));
            return root;
        }

        public static MPart BedFoot()
        {
            var root = new MPart("bed_foot");
            root.Add(new MPart("main").Box(0, 22, 0, 0, 0, 16, 16, 6));
            root.Add(new MPart("left_leg").Box(50, 0, 0, 6, -16, 3, 3, 3).At(0, 0, 0, Mathf.PI / 2, 0, 0));
            root.Add(new MPart("right_leg").Box(50, 12, -16, 6, -16, 3, 3, 3).At(0, 0, 0, Mathf.PI / 2, 0, Mathf.PI * 1.5f));
            return root;
        }
    }
}
