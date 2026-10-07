using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Blocks Minecraft draws with a block entity renderer instead of a JSON model: chests (with their opening
    /// lid), ender chests, shulker boxes and beds. Their entity models are built from Minecraft's own model data
    /// and placed with the renderer's matrix chain, converted to Unity space. Blocks whose model is "builtin/entity"
    /// without a renderer here (signs, banners, skulls, decorated pots) show a plain cube of their particle texture.
    /// </summary>
    public static class Special
    {
        private static readonly Dictionary<Vector3Int, MPart> lids = new Dictionary<Vector3Int, MPart>();
        private static readonly Dictionary<Vector3Int, MPart> locks = new Dictionary<Vector3Int, MPart>();

        public static bool Handles(BState s)
        {
            switch (s.Block.Class)
            {
                case "ChestBlock": case "TrappedChestBlock": case "EnderChestBlock": case "ShulkerBoxBlock": case "BedBlock":
                    return true;
            }
            var models = McData.ModelsFor(s);
            if (models.Count == 0) return false;
            if (!McData.Models.TryGetValue(models[0].Model, out var m)) return false;
            return m["elements"] == null && m["particle"] != null && m["layers"] == null;
        }

        /// <summary>Unity transform for a Minecraft block-entity matrix chain applied to a model built with MPart's v -> -v mapping.</summary>
        private static void Place(Transform t, Matrix4x4 mc, Vector3Int cell)
        {
            // Unity = Mz * mc * v (block space, z mirrored) ; MPart geometry is -v  =>  P = Mz * mc * (-I)
            var mz = Matrix4x4.Scale(new Vector3(1, 1, -1));
            var p = mz * mc * Matrix4x4.Scale(-Vector3.one);
            var pos = (Vector3)p.GetColumn(3) + new Vector3(cell.x, cell.y, cell.z + 1f);
            Vector3 c0 = p.GetColumn(0), c1 = p.GetColumn(1), c2 = p.GetColumn(2);
            t.position = pos;
            t.rotation = Quaternion.LookRotation(c2, c1);
            t.localScale = new Vector3(c0.magnitude, c1.magnitude, c2.magnitude);
        }

        private static Matrix4x4 T(float x, float y, float z) => Matrix4x4.Translate(new Vector3(x, y, z));
        private static Matrix4x4 RY(float deg) => Matrix4x4.Rotate(Quaternion.AngleAxis(deg, Vector3.up));
        private static Matrix4x4 RX(float deg) => Matrix4x4.Rotate(Quaternion.AngleAxis(deg, Vector3.right));
        private static Matrix4x4 RZ(float deg) => Matrix4x4.Rotate(Quaternion.AngleAxis(deg, Vector3.forward));

        public static float AsRotation(Dir d) => BlockRules.Horiz(d) * 90f; // Direction.asRotation: south 0, west 90, north 180, east 270

        /// <summary>Direction.getRotationQuaternion() as a matrix (used by shulker boxes, end rods...).</summary>
        private static Matrix4x4 DirRot(Dir d)
        {
            switch (d)
            {
                case Dir.Down: return RX(180);
                case Dir.Up: return Matrix4x4.identity;
                case Dir.North: return RX(90) * RZ(180);
                case Dir.South: return RX(90);
                case Dir.West: return RX(90) * RZ(90);
                default: return RX(90) * RZ(-90);
            }
        }

        public static GameObject Build(BState s, Vector3Int cell)
        {
            var go = new GameObject("special " + s.Block.Id);
            string cls = s.Block.Class;
            if (cls == "ChestBlock" || cls == "TrappedChestBlock" || cls == "EnderChestBlock")
            {
                var tex = cls == "EnderChestBlock" ? "entity/chest/ender" : cls == "TrappedChestBlock" ? "entity/chest/trapped" : "entity/chest/normal";
                var model = Models.Chest();
                var inst = EntityModelInstanceRaw(model, McTex.Get(tex), go.transform, 64, 64);
                float rot = AsRotation(BlockRules.ParseDir(s["facing"]));
                Place(inst, T(0.5f, 0.5f, 0.5f) * RY(-rot) * T(-0.5f, -0.5f, -0.5f), cell);
                lids[cell] = model.Find("lid");
                locks[cell] = model.Find("lock");
            }
            else if (cls == "ShulkerBoxBlock")
            {
                var color = s.Block.Id == "shulker_box" ? "" : "_" + s.Block.Id.Replace("_shulker_box", "");
                var model = Models.Shulker();
                var inst = EntityModelInstanceRaw(model, McTex.Get("entity/shulker/shulker" + color), go.transform, 64, 64);
                var f = BlockRules.ParseDir(s["facing"] ?? "up");
                Place(inst, T(0.5f, 0.5f, 0.5f) * Matrix4x4.Scale(Vector3.one * 0.9995f) * DirRot(f) * Matrix4x4.Scale(new Vector3(1, -1, -1)) * T(0, -1, 0), cell);
                lids[cell] = model.Find("lid");
            }
            else if (cls == "BedBlock")
            {
                var color = s.Block.Id.Replace("_bed", "");
                bool head = s["part"] == "head";
                var model = head ? Models.BedHead() : Models.BedFoot();
                var inst = EntityModelInstanceRaw(model, McTex.Get("entity/bed/" + color), go.transform, 64, 64);
                float rot = AsRotation(BlockRules.ParseDir(s["facing"]));
                Place(inst, T(0, 0.5625f, 0) * RX(90) * T(0.5f, 0.5f, 0.5f) * RZ(180 + rot) * T(-0.5f, -0.5f, -0.5f), cell);
            }
            else
            {
                // builtin/entity without a renderer of ours: a cube of the particle texture
                var models = McData.ModelsFor(s);
                var tex = (string)McData.Models[models[0].Model]["particle"];
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.Destroy(cube.GetComponent<Collider>());
                cube.transform.SetParent(go.transform, false);
                cube.transform.position = BlockWorld.Center(cell);
                cube.transform.localScale = Vector3.one * 0.6f;
                var mat = BlockWorld.MakeMat(1, Atlas.Source(tex));
                cube.GetComponent<Renderer>().sharedMaterial = mat;
            }
            return go;
        }

        private static Transform EntityModelInstanceRaw(MPart model, Texture2D tex, Transform parent, float tw, float th)
        {
            var holder = new GameObject(model.Name);
            holder.transform.SetParent(parent, false);
            var mat = BlockWorld.MakeMat(1, tex);
            model.Build(holder.transform, mat, tw, th, BlockWorld.Layer);
            return holder.transform;
        }

        /// <summary>Animates chest lids (ChestLidAnimator: +-0.1 per tick, eased with 1-(1-x)^3) and shulker lids.</summary>
        public static void Animate(float dt)
        {
            foreach (var kv in new List<KeyValuePair<Vector3Int, MPart>>(lids))
            {
                var lid = kv.Value;
                if (lid == null || lid.T == null) { lids.Remove(kv.Key); continue; }
                var e = BlockWorld.Entity(kv.Key);
                if (e == null) continue;
                float before = e.LidOpen;
                e.LidOpen = Mathf.MoveTowards(e.LidOpen, e.LidTarget, dt * 2f);
                if (before == e.LidOpen && before == 0) continue;
                var s = BlockWorld.Get(kv.Key);
                if (s == null) continue;
                float f = 1f - e.LidOpen;
                f = 1f - f * f * f;
                if (s.Block.Class == "ShulkerBoxBlock")
                {
                    lid.T.localPosition = -new Vector3(0, 24f - e.LidOpen * 0.5f * 16f, 0) / 16f;
                    lid.T.localRotation = MPart.Rot(0, 270f * e.LidOpen * Mathf.Deg2Rad, 0);
                }
                else
                {
                    lid.Pose(-(f * Mathf.PI / 2f), 0, 0);
                    if (locks.TryGetValue(kv.Key, out var lk) && lk != null) lk.Pose(-(f * Mathf.PI / 2f), 0, 0);
                }
            }
        }

        public static void Forget(Vector3Int c) { lids.Remove(c); locks.Remove(c); }
    }
}
