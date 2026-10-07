using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// What Minecraft needs to know about Subnautica's world: where the water surface is for the player, what is
    /// solid, what the ground is made of (for step sounds and for mining), and the player's Subnautica equipment
    /// that has a Minecraft meaning (fins = Depth Strider, ultra glide fins = Dolphin's Grace, seaglide push,
    /// reinforced suit = armour).
    /// </summary>
    public static class SnWorld
    {
        public static int SolidMask => ~((1 << LayerID.Player) | (1 << LayerID.Trigger) | (1 << LayerID.UI) | (1 << LayerMask.NameToLayer("Ignore Raycast")));

        /// <summary>Water surface height for the player, like Player.UpdateIsUnderwater (lifepod and dry bases: none; aquariums: full).</summary>
        public static float WaterLevelAt(Player p)
        {
            if (p == null) return float.NegativeInfinity;
            if (p.escapePod.value) return float.NegativeInfinity;
            if (p.currentWaterPark != null) return float.PositiveInfinity;
            if (p.currentSub != null) return p.currentSub.IsUnderwater(p.transform.position) ? float.PositiveInfinity : float.NegativeInfinity;
            if (p.waterPlaneOverride != null && !p.waterPlaneOverride.Equals(null)) return p.waterPlaneOverride.GetWaterLevel();
            if (p.precursorOutOfWater) return float.NegativeInfinity;
            return Ocean.GetOceanLevel();
        }

        /// <summary>Is a world point under Subnautica's ocean surface (and not inside a base)?</summary>
        public static bool IsWater(Vector3 pt)
        {
            if (pt.y >= Ocean.GetOceanLevel()) return false;
            var p = Player.main;
            if (p != null && p.currentSub != null && p.currentSub.isBase && (p.transform.position - pt).sqrMagnitude < 400f) return p.currentSub.IsUnderwater(pt);
            return true;
        }

        public static BState BlockUnder(Vector3 feet)
        {
            var s = BlockWorld.Get(BlockWorld.CellAt(feet + Vector3.down * 0.1f));
            if (s != null && s.Collision.Length > 0) return s;
            s = BlockWorld.Get(BlockWorld.CellAt(feet + Vector3.down * 0.6f));
            return s != null && s.Collision.Length > 0 ? s : null;
        }

        public static VFXSurfaceTypes SurfaceAt(RaycastHit hit)
        {
            var t = Utils.GetObjectSurfaceType(hit.collider.gameObject);
            if (t == VFXSurfaceTypes.none) t = Utils.GetTerrainSurfaceType(hit.point, hit.normal);
            return t;
        }

        public static bool IsTerrain(Collider c) => c != null && (c.gameObject.layer == LayerID.TerrainCollider || c.GetComponentInParent<TerrainChunkPiece>() != null || c.name.StartsWith("Chunk"));

        public static string SurfaceSound(Vector3 feet)
        {
            if (!Physics.Raycast(feet + Vector3.up * 0.3f, Vector3.down, out var hit, 0.8f, SolidMask, QueryTriggerInteraction.Ignore)) return "stone";
            return SoundFor(SurfaceAt(hit));
        }

        public static string SoundFor(VFXSurfaceTypes t)
        {
            switch (t)
            {
                case VFXSurfaceTypes.sand: return "sand";
                case VFXSurfaceTypes.metal: case VFXSurfaceTypes.electronic: return "metal";
                case VFXSurfaceTypes.wood: return "wood";
                case VFXSurfaceTypes.vegetation: case VFXSurfaceTypes.organic: return "grass";
                case VFXSurfaceTypes.coral: return "coral_block";
                case VFXSurfaceTypes.glass: return "glass";
                case VFXSurfaceTypes.ionCrystal: return "amethyst_block";
                default: return "stone";
            }
        }

        /// <summary>
        /// The Minecraft block a spot of Subnautica's world is "made of" when mined: sand floors give sand, rock gives
        /// stone (deepslate deeper down, basalt in the lava zones), with Minecraft ores mixed in by depth like in
        /// Minecraft's world generation, plants give dirt and grass, corals give coral blocks, trees give logs.
        /// Subnautica's streamed terrain cannot be dug, so the ground stays as it is.
        /// </summary>
        public static BlockDef MinedBlock(RaycastHit hit, out bool isOre)
        {
            isOre = false;
            var t = SurfaceAt(hit);
            float depth = -hit.point.y;
            string mat = null;
            try { mat = MaterialDatabase.GetTerrainMaterial(hit.point, hit.normal); } catch { }
            bool lava = mat != null && mat.ToLowerInvariant().Contains("lava");
            switch (t)
            {
                case VFXSurfaceTypes.sand:
                    return McData.Block(depth > 900 ? "soul_sand" : depth < 0 ? "sand" : (Random.value < 0.15f ? "gravel" : "sand"));
                case VFXSurfaceTypes.vegetation:
                case VFXSurfaceTypes.organic:
                    return McData.Block(depth < 0 ? "grass_block" : Random.value < 0.3f ? "moss_block" : "dirt");
                case VFXSurfaceTypes.coral:
                {
                    string[] c = { "brain_coral_block", "tube_coral_block", "bubble_coral_block", "fire_coral_block", "horn_coral_block" };
                    return McData.Block(c[Mathf.Abs(Mathf.FloorToInt(hit.point.x * 0.37f + hit.point.z * 0.61f)) % c.Length]);
                }
                case VFXSurfaceTypes.wood: return McData.Block("oak_log");
                case VFXSurfaceTypes.ionCrystal: return McData.Block("amethyst_cluster");
                case VFXSurfaceTypes.metal:
                case VFXSurfaceTypes.electronic: return McData.Block("iron_block");
                case VFXSurfaceTypes.glass: return McData.Block("glass");
            }
            // rock
            bool deep = depth > 300;
            if (lava) return McData.Block(Random.value < 0.15f ? "magma_block" : Random.value < 0.5f ? "basalt" : "blackstone");
            var ore = Ore(depth);
            if (ore != null)
            {
                isOre = true;
                var b = McData.Block((deep ? "deepslate_" : "") + ore);
                if (b != null) return b;
            }
            if (depth < 0) return McData.Block("stone");
            if (deep) return McData.Block(depth > 900 ? "tuff" : "deepslate");
            float r = Mathf.PerlinNoise(hit.point.x * 0.05f, hit.point.z * 0.05f);
            return McData.Block(r < 0.15f ? "granite" : r > 0.85f ? "diorite" : r > 0.75f ? "andesite" : "stone");
        }

        /// <summary>Ore chances per mined rock, growing with depth roughly like Minecraft's ore placement by height.</summary>
        private static string Ore(float depth)
        {
            float r = Random.value;
            float acc = 0;
            bool Pick(float chance) { acc += chance; return r < acc; }
            if (Pick(depth < 150 ? 0.04f : 0.01f)) return "coal_ore";
            if (Pick(0.02f)) return "copper_ore";
            if (Pick(depth > 30 ? 0.025f : 0.01f)) return "iron_ore";
            if (depth > 100 && Pick(0.008f)) return "gold_ore";
            if (depth > 200 && Pick(0.01f)) return "redstone_ore";
            if (depth > 100 && Pick(0.006f)) return "lapis_ore";
            if (depth > 400 && Pick(0.004f)) return "diamond_ore";
            if (depth > 150 && Pick(0.0012f)) return "emerald_ore";
            return null;
        }

        // ------------------------------------------------------------------------------------------------ equipment

        private static bool Equipped(TechType t)
        {
            try { return global::Inventory.main != null && global::Inventory.main.equipment.GetCount(t) > 0; } catch { return false; }
        }

        public static bool HasFins() => Equipped(TechType.Fins) || Equipped(TechType.SwimChargeFins);
        public static bool HasUltraGlideFins() => Equipped(TechType.UltraGlideFins);
        public static bool WearsReinforcedSuit() => Equipped(TechType.ReinforcedDiveSuit);

        /// <summary>The seaglide pulls like a much stronger Depth Strider while it has power and the player swims forward.</summary>
        public static float SeaglideBoost(float forward)
        {
            if (forward <= 0.1f) return 1f;
            var tool = SnBridge.HeldTool;
            if (tool == null || !(tool is Seaglide)) return 1f;
            var e = tool.GetComponent<EnergyMixin>();
            if (e != null && e.charge <= 0) return 1f;
            return 2.75f;
        }
    }
}
