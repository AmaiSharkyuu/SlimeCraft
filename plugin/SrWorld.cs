using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// What Minecraft needs to know about Slime Rancher's world: what is solid, where the ponds and the Slime Sea are,
    /// what the ground is made of (for step sounds and for mining) and where the player stands.
    /// </summary>
    public static class SrWorld
    {
        /// <summary>Everything Slime Rancher's own player controller collides with (its layer mask).</summary>
        public static int SolidMask => -675375893;

        private const int WaterLayer = 4;

        public static GameObject Player => SRSingleton<SceneContext>.Instance != null ? SRSingleton<SceneContext>.Instance.Player : null;

        public static Vector3? PlayerPos => Player != null ? Player.transform.position : (Vector3?)null;

        public static vp_FPController Controller => Player != null ? Player.GetComponent<vp_FPController>() : null;

        /// <summary>The player's feet (Slime Rancher's player transform sits at the feet).</summary>
        public static Vector3 Feet => Player != null ? Player.transform.position : Vector3.zero;

        /// <summary>The player's eyes, at Minecraft's eye height for the current pose.</summary>
        public static Vector3 Eye => Feet + Vector3.up * Motor.EyeHeight;

        /// <summary>Frees the mouse for a Minecraft screen (and stops Slime Rancher's mouse look), or captures it again.</summary>
        public static void FreeCursor(bool free)
        {
            var input = Player != null ? Player.GetComponent<vp_FPInput>() : null;
            if (input != null) { input.MouseCursorForced = free; input.MouseCursorBlocksMouseLook = true; }
            vp_Utility.LockCursor = !free;
            Cursor.visible = free;
            if (free) Cursor.lockState = CursorLockMode.None;
        }

        /// <summary>Moves the player's feet somewhere else (commands, ender pearls, chorus fruit, respawn).</summary>
        public static void Teleport(Vector3 feet)
        {
            var c = Controller;
            if (c != null) c.SetPosition(feet);
        }

        /// <summary>Is a world point inside one of Slime Rancher's water volumes (ponds, rivers, the Slime Sea)?</summary>
        public static bool IsWater(Vector3 pt) => Physics.CheckSphere(pt, 0.05f, 1 << WaterLayer, QueryTriggerInteraction.Collide);

        /// <summary>Height of the water surface above a point, or -infinity when the point is not in water.</summary>
        public static float WaterLevelAt(Vector3 pos)
        {
            var cols = Physics.OverlapSphere(pos, 0.1f, 1 << WaterLayer, QueryTriggerInteraction.Collide);
            float top = float.NegativeInfinity;
            foreach (var c in cols) top = Mathf.Max(top, c.bounds.max.y);
            if (float.IsNegativeInfinity(top)) return top;
            // Water volumes are boxes or meshes: find the real surface right above the point when there is one.
            var from = new Vector3(pos.x, top + 0.5f, pos.z);
            if (Physics.Raycast(from, Vector3.down, out var hit, top - pos.y + 0.6f, 1 << WaterLayer, QueryTriggerInteraction.Collide)) return hit.point.y;
            return top;
        }

        // ------------------------------------------------------------------------------------------------ actors

        private static readonly System.Collections.Generic.Dictionary<int, float> tarrDamage = new System.Collections.Generic.Dictionary<int, float>();

        /// <summary>The Slime Rancher actor (slime, plort, chicken...) a collider belongs to, if it has a body.</summary>
        public static Identifiable ActorOf(Collider c)
        {
            if (c == null) return null;
            var id = c.GetComponentInParent<Identifiable>();
            return id != null && id.GetComponent<Rigidbody>() != null ? id : null;
        }

        /// <summary>
        /// Minecraft damage to a Slime Rancher actor: it is knocked back (push in blocks per tick, like Minecraft's
        /// knockback), slimes squeal, and tarrs, which no water gun washes away any more, pop once they have taken
        /// 10 Minecraft health points of damage.
        /// </summary>
        public static void HurtActor(Identifiable a, float dmg, Vector3 push)
        {
            if (a == null) return;
            var rb = a.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic) rb.AddForce(push * 20f, ForceMode.VelocityChange);
            var id = a.id;
            var at = a.transform.position;
            if (Identifiable.IsSlime(id)) Sounds.Play(Identifiable.IsLargo(id) ? "entity.slime.hurt" : "entity.slime.hurt_small", at);
            if (!Identifiable.IsTarr(id) || dmg <= 0) return;
            int key = a.gameObject.GetInstanceID();
            tarrDamage.TryGetValue(key, out var taken);
            taken += dmg;
            if (taken < 10f) { tarrDamage[key] = taken; return; }
            tarrDamage.Remove(key);
            Particles.Smoke(at);
            Sounds.Play("entity.slime.death", at);
            Destroyer.DestroyActor(a.gameObject, "SlimeCraft.Hurt");
            XpOrb.Spawn(at, 3);
        }

        /// <summary>An explosion's push and damage on every Slime Rancher actor around (like Explosion.affectWorld on entities).</summary>
        public static void ExplodeActors(Vector3 at, float q)
        {
            var done = new System.Collections.Generic.HashSet<Identifiable>();
            foreach (var c in Physics.OverlapSphere(at, q, ~0, QueryTriggerInteraction.Ignore))
            {
                var a = ActorOf(c);
                if (a == null || !done.Add(a)) continue;
                float dist = Vector3.Distance(a.transform.position, at) / q;
                float v = Mathf.Clamp01(1f - dist);
                HurtActor(a, (v * v + v) / 2f * 7f * q + 1f, (a.transform.position - at).normalized * v + Vector3.up * v * 0.5f);
            }
        }

        public static BState BlockUnder(Vector3 feet)
        {
            var s = BlockWorld.Get(BlockWorld.CellAt(feet + Vector3.down * 0.1f));
            if (s != null && s.Collision.Length > 0) return s;
            s = BlockWorld.Get(BlockWorld.CellAt(feet + Vector3.down * 0.6f));
            return s != null && s.Collision.Length > 0 ? s : null;
        }

        /// <summary>What a Slime Rancher surface is made of, guessed from its object and physics material names.</summary>
        public static string SurfaceAt(RaycastHit hit)
        {
            var c = hit.collider;
            if (c == null) return "stone";
            var n = ((c.sharedMaterial != null ? c.sharedMaterial.name : "") + " " + c.name + " " + (c.transform.parent != null ? c.transform.parent.name : "")).ToLowerInvariant();
            if (n.Contains("wood") || n.Contains("plank") || n.Contains("deck") || n.Contains("bridge") || n.Contains("stair") || n.Contains("fence") || n.Contains("crate")) return "wood";
            if (n.Contains("tree") || n.Contains("bark") || n.Contains("trunk") || n.Contains("log")) return "log";
            if (n.Contains("sand") || n.Contains("beach") || n.Contains("desert") || n.Contains("dune")) return "sand";
            if (n.Contains("metal") || n.Contains("tech") || n.Contains("ranch") && n.Contains("tech") || n.Contains("pipe") || n.Contains("silo")) return "metal";
            if (n.Contains("glass") || n.Contains("crystal")) return "glass";
            if (n.Contains("mushroom") || n.Contains("shroom")) return "mushroom";
            if (n.Contains("grass") || n.Contains("moss") || n.Contains("leaf") || n.Contains("leaves") || n.Contains("bush")) return "grass";
            if (n.Contains("rock") || n.Contains("cliff") || n.Contains("stone") || n.Contains("mtn") || n.Contains("ruin") || n.Contains("boulder")) return "stone";
            if (c.gameObject.layer == 12) return "stone"; // "Mountain"
            return "dirt";
        }

        public static bool IsTerrain(Collider c)
        {
            if (c == null || c.isTrigger) return false;
            if (c.GetComponentInParent<Identifiable>() != null || BlockWorld.CellOf(c, out _)) return false;
            if (c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic) return false;
            int l = c.gameObject.layer;
            return l == 0 || l == 12 || l == 28;
        }

        public static string SurfaceSound(Vector3 feet)
        {
            if (!Physics.Raycast(feet + Vector3.up * 0.3f, Vector3.down, out var hit, 0.8f, SolidMask, QueryTriggerInteraction.Ignore)) return "stone";
            switch (SurfaceAt(hit))
            {
                case "wood": case "log": return "wood";
                case "sand": return "sand";
                case "metal": return "metal";
                case "glass": return "glass";
                case "grass": return "grass";
                case "mushroom": return "grass";
                case "dirt": return "gravel";
                default: return "stone";
            }
        }

        /// <summary>
        /// The Minecraft block a spot of Slime Rancher's world is "made of" when mined: rock gives stone (with ores
        /// mixed in like a Minecraft cave wall), sand gives sand, the Far, Far Range's red earth gives dirt and
        /// coarse dirt, trees give logs, bridges give planks. The ranch's own ground cannot be dug, so it stays.
        /// </summary>
        public static BlockDef MinedBlock(RaycastHit hit, out bool isOre)
        {
            isOre = false;
            switch (SurfaceAt(hit))
            {
                case "sand": return McData.Block(Random.value < 0.15f ? "gravel" : "sand");
                case "wood": return McData.Block("oak_planks");
                case "log": return McData.Block("oak_log");
                case "metal": return McData.Block("iron_block");
                case "glass": return McData.Block("glass");
                case "mushroom": return McData.Block(Random.value < 0.5f ? "red_mushroom_block" : "mushroom_stem");
                case "grass": return McData.Block(Random.value < 0.3f ? "moss_block" : "grass_block");
                case "dirt": return McData.Block(Random.value < 0.25f ? "coarse_dirt" : Random.value < 0.1f ? "clay" : "dirt");
            }
            var ore = Ore();
            if (ore != null)
            {
                isOre = true;
                var b = McData.Block(ore);
                if (b != null) return b;
            }
            float r = Mathf.PerlinNoise(hit.point.x * 0.05f, hit.point.z * 0.05f);
            return McData.Block(r < 0.15f ? "granite" : r > 0.85f ? "diorite" : r > 0.75f ? "andesite" : "stone");
        }

        /// <summary>Ore chances per mined rock, about what a Minecraft cave wall shows.</summary>
        private static string Ore()
        {
            float r = Random.value;
            float acc = 0;
            bool Pick(float chance) { acc += chance; return r < acc; }
            if (Pick(0.05f)) return "coal_ore";
            if (Pick(0.03f)) return "copper_ore";
            if (Pick(0.03f)) return "iron_ore";
            if (Pick(0.01f)) return "gold_ore";
            if (Pick(0.015f)) return "redstone_ore";
            if (Pick(0.008f)) return "lapis_ore";
            if (Pick(0.004f)) return "diamond_ore";
            if (Pick(0.0015f)) return "emerald_ore";
            return null;
        }

        // Subnautica's diving gear has no Slime Rancher counterpart.
        public static bool HasFins() => false;
        public static bool HasUltraGlideFins() => false;
        public static bool WearsReinforcedSuit() => false;
        public static float SeaglideBoost(float forward) => 1f;
    }
}
