using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Minecraft's block breaking speed: AbstractBlock.calcBlockBreakingDelta and PlayerEntity.getBlockBreakingSpeed,
    /// with the tool's mining speed only on the blocks its tag covers, Efficiency, Haste / Mining Fatigue, the
    /// underwater penalty (lifted by Aqua Affinity, or by Subnautica's rebreather) and the penalty for floating.
    /// </summary>
    public static class Mining
    {
        public static float ToolSpeed(ItemStack tool, BlockDef b)
        {
            if (tool == null || !tool.IsMc) return 1f;
            var it = tool.Item;
            if (it.Mineable != null) return b.Tags.Contains(it.Mineable) ? it.ToolSpeed : 1f;
            if (it.Class == "SwordItem") return b.Id == "cobweb" ? 15f : b.Tags.Contains("sword_efficient") ? 1.5f : 1f;
            if (it.Class == "ShearsItem")
            {
                if (b.Id == "cobweb" || b.Tags.Contains("leaves")) return 15f;
                if (b.Tags.Contains("wool")) return 5f;
                if (b.Id == "vine" || b.Id == "glow_lichen") return 2f;
                return 1f;
            }
            return 1f;
        }

        public static bool IsSuitable(ItemStack tool, BlockDef b)
        {
            if (tool == null || !tool.IsMc) return false;
            var it = tool.Item;
            if (it.Mineable != null)
            {
                int lvl = it.ToolLevel;
                if (lvl < 3 && b.Tags.Contains("needs_diamond_tool")) return false;
                if (lvl < 2 && b.Tags.Contains("needs_iron_tool")) return false;
                if (lvl < 1 && b.Tags.Contains("needs_stone_tool")) return false;
                return b.Tags.Contains(it.Mineable);
            }
            if (it.Class == "SwordItem") return b.Id == "cobweb";
            if (it.Class == "ShearsItem") return b.Id == "cobweb" || b.Id == "redstone_wire" || b.Id == "tripwire";
            return false;
        }

        public static bool AquaAffinity()
        {
            var h = Inventory.ArmorIn("head");
            if (h != null && h.Level("aqua_affinity") > 0) return true;
            return false;
        }

        public static float BreakingSpeed(BlockDef b, ItemStack tool)
        {
            float f = ToolSpeed(tool, b);
            if (f > 1f && tool != null)
            {
                int i = tool.Level("efficiency");
                if (i > 0) f += i * i + 1;
            }
            int haste = Mathf.Max(Effects.Amplifier("haste"), Effects.Has("conduit_power") ? Effects.Amplifier("conduit_power") : -1);
            if (haste >= 0) f *= 1f + (haste + 1) * 0.2f;
            if (Effects.Has("mining_fatigue"))
            {
                switch (Effects.Amplifier("mining_fatigue")) { case 0: f *= 0.3f; break; case 1: f *= 0.09f; break; case 2: f *= 0.0027f; break; default: f *= 8.1E-4f; break; }
            }
            if (Motor.Submerged && !AquaAffinity()) f /= 5f;
            if (!Motor.OnGround) f /= 5f;
            return f;
        }

        /// <summary>Progress per tick (1 = broken).</summary>
        public static float Delta(BlockDef b, ItemStack tool)
        {
            float h = b.Hardness;
            if (h < 0) return 0f;
            if (h == 0) return 1f;
            bool harvest = !b.ToolRequired || IsSuitable(tool, b);
            return BreakingSpeed(b, tool) / h / (harvest ? 30f : 100f);
        }
    }
}
