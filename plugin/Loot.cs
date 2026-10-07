using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Minecraft's loot tables evaluated from the jar's own JSON: pools and rolls, item / alternatives / tag
    /// entries, the conditions blocks and mobs use (match_tool with Silk Touch and items, survives_explosion,
    /// block_state_property, random_chance(_with_looting), table_bonus, killed_by_player, inverted, any_of) and the
    /// functions (set_count, apply_bonus for Fortune, explosion_decay, limit_count, looting_enchant, furnace_smelt).
    /// </summary>
    public static class Loot
    {
        public class Ctx
        {
            public ItemStack Tool;
            public BState State;
            public bool Explosion;
            public bool KilledByPlayer = true;
            public bool OnFire;
            public int Looting;
        }

        public static List<ItemStack> Roll(JObject table, Ctx ctx)
        {
            var result = new List<ItemStack>();
            if (table == null || !(table["pools"] is JArray pools)) return result;
            foreach (JObject pool in pools)
            {
                if (!Conditions(pool["conditions"], ctx)) continue;
                int rolls = Mathf.RoundToInt(Number(pool["rolls"], 1));
                rolls += Mathf.FloorToInt(Number(pool["bonus_rolls"], 0));
                for (int r = 0; r < rolls; r++)
                {
                    var picked = PickEntry((JArray)pool["entries"], ctx);
                    if (picked == null) continue;
                    var stacks = Expand(picked, ctx);
                    foreach (var st in stacks)
                    {
                        Functions(pool["functions"], st, ctx);
                        if (st.Count > 0) result.Add(st);
                    }
                }
            }
            return result;
        }

        private static JObject PickEntry(JArray entries, Ctx ctx)
        {
            var ok = new List<JObject>();
            int total = 0;
            foreach (JObject e in entries)
            {
                if (!Conditions(e["conditions"], ctx)) continue;
                var type = ((string)e["type"]).Replace("minecraft:", "");
                if (type == "alternatives")
                {
                    foreach (JObject child in (JArray)e["children"])
                        if (Conditions(child["conditions"], ctx)) { ok.Add(child); total += (int?)child["weight"] ?? 1; break; }
                    continue;
                }
                if (type == "group" || type == "sequence") { ok.Add(e); total += 1; continue; }
                ok.Add(e);
                total += (int?)e["weight"] ?? 1;
            }
            if (ok.Count == 0) return null;
            int roll = Random.Range(0, total);
            foreach (var e in ok)
            {
                roll -= (int?)e["weight"] ?? 1;
                if (roll < 0) return e;
            }
            return ok[0];
        }

        private static List<ItemStack> Expand(JObject e, Ctx ctx)
        {
            var list = new List<ItemStack>();
            var type = ((string)e["type"]).Replace("minecraft:", "");
            switch (type)
            {
                case "item":
                {
                    var s = ItemStack.Of((string)e["name"]);
                    if (s != null) { Functions(e["functions"], s, ctx); list.Add(s); }
                    break;
                }
                case "tag":
                {
                    var tag = ((string)e["name"]).Replace("minecraft:", "");
                    if (McData.ItemTags.TryGetValue(tag, out var ids))
                    {
                        var arr = new List<string>(ids);
                        if (arr.Count > 0)
                        {
                            var s = ItemStack.Of(arr[Random.Range(0, arr.Count)]);
                            if (s != null) { Functions(e["functions"], s, ctx); list.Add(s); }
                        }
                    }
                    break;
                }
                case "loot_table":
                {
                    var name = ((string)e["name"]).Replace("minecraft:", "");
                    if (name.StartsWith("blocks/")) { var b = McData.Block(name.Substring(7)); if (b?.Loot != null) list.AddRange(Roll(b.Loot, ctx)); }
                    else if (name.StartsWith("entities/") && McData.EntityLoot.TryGetValue(name.Substring(9), out var t)) list.AddRange(Roll(t, ctx));
                    else if (McData.EntityLoot.TryGetValue(name, out var g)) list.AddRange(Roll(g, ctx));
                    break;
                }
                case "group":
                case "sequence":
                    foreach (JObject child in (JArray)e["children"])
                        if (Conditions(child["conditions"], ctx)) list.AddRange(Expand(child, ctx));
                    break;
            }
            return list;
        }

        private static float Number(JToken t, float def)
        {
            if (t == null) return def;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return (float)t;
            var type = ((string)t["type"] ?? "uniform").Replace("minecraft:", "");
            switch (type)
            {
                case "constant": return (float)t["value"];
                case "uniform": return Random.Range(Number(t["min"], 0), Number(t["max"], 1));
                case "binomial":
                {
                    int n = Mathf.RoundToInt(Number(t["n"], 1)); float p = Number(t["p"], 0.5f); int k = 0;
                    for (int i = 0; i < n; i++) if (Random.value < p) k++;
                    return k;
                }
            }
            return def;
        }

        // ------------------------------------------------------------------------------------------------ conditions

        private static bool Conditions(JToken conds, Ctx ctx)
        {
            if (!(conds is JArray a)) return true;
            foreach (JObject c in a) if (!Condition(c, ctx)) return false;
            return true;
        }

        private static bool Condition(JObject c, Ctx ctx)
        {
            var type = ((string)c["condition"]).Replace("minecraft:", "");
            switch (type)
            {
                case "survives_explosion": return !ctx.Explosion || Random.value < 1f / 4f;
                case "inverted": return !Condition((JObject)c["term"], ctx);
                case "alternative":
                case "any_of":
                    foreach (JObject t in (JArray)c["terms"]) if (Condition(t, ctx)) return true;
                    return false;
                case "all_of":
                    foreach (JObject t in (JArray)c["terms"]) if (!Condition(t, ctx)) return false;
                    return true;
                case "random_chance": return Random.value < (float)c["chance"];
                case "random_chance_with_looting": return Random.value < (float)c["chance"] + ctx.Looting * (float)c["looting_multiplier"];
                case "killed_by_player": return ctx.KilledByPlayer;
                case "entity_properties":
                    // "on fire" checks for cooked drops
                    if (c["predicate"]?["flags"]?["is_on_fire"] != null) return ctx.OnFire == (bool)c["predicate"]["flags"]["is_on_fire"];
                    return true;
                case "match_tool": return MatchTool((JObject)c["predicate"], ctx.Tool);
                case "block_state_property":
                {
                    if (ctx.State == null) return false;
                    var props = c["properties"] as JObject;
                    if (props == null) return true;
                    foreach (var kv in props)
                    {
                        var v = ctx.State[kv.Key];
                        if (kv.Value.Type == JTokenType.Object)
                        {
                            int iv = int.TryParse(v, out var x) ? x : 0;
                            if (kv.Value["min"] != null && iv < (int)kv.Value["min"]) return false;
                            if (kv.Value["max"] != null && iv > (int)kv.Value["max"]) return false;
                        }
                        else if (v != (string)kv.Value) return false;
                    }
                    return true;
                }
                case "table_bonus":
                {
                    var chances = c["chances"].ToObject<float[]>();
                    int lvl = ctx.Tool != null ? ctx.Tool.Level(((string)c["enchantment"]).Replace("minecraft:", "")) : 0;
                    return Random.value < chances[Mathf.Min(lvl, chances.Length - 1)];
                }
                case "location_check": case "weather_check": case "time_check": case "damage_source_properties": case "value_check":
                    return true;
            }
            return true;
        }

        private static bool MatchTool(JObject pred, ItemStack tool)
        {
            if (pred == null) return true;
            if (pred["items"] is JArray items)
            {
                if (tool == null || !tool.IsMc) return false;
                bool any = false;
                foreach (var i in items) if (((string)i).Replace("minecraft:", "") == tool.Item.Id) any = true;
                if (!any) return false;
            }
            if (pred["tag"] != null)
            {
                if (tool == null || !tool.IsMc || !tool.Item.Tags.Contains(((string)pred["tag"]).Replace("minecraft:", ""))) return false;
            }
            if (pred["enchantments"] is JArray ench)
            {
                foreach (JObject e in ench)
                {
                    var id = ((string)e["enchantment"]).Replace("minecraft:", "");
                    int lvl = tool != null ? tool.Level(id) : 0;
                    int min = e["levels"]?["min"] != null ? (int)e["levels"]["min"] : 1;
                    if (lvl < min) return false;
                }
            }
            return true;
        }

        // ------------------------------------------------------------------------------------------------ functions

        private static void Functions(JToken fns, ItemStack s, Ctx ctx)
        {
            if (!(fns is JArray a)) return;
            foreach (JObject f in a)
            {
                if (!Conditions(f["conditions"], ctx)) continue;
                var type = ((string)f["function"]).Replace("minecraft:", "");
                switch (type)
                {
                    case "set_count":
                    {
                        int n = Mathf.FloorToInt(Number(f["count"], 1));
                        if (f["add"] != null && (bool)f["add"]) s.Count += n; else s.Count = n;
                        break;
                    }
                    case "apply_bonus":
                    {
                        int lvl = ctx.Tool != null ? ctx.Tool.Level(((string)f["enchantment"]).Replace("minecraft:", "")) : 0;
                        var formula = ((string)f["formula"]).Replace("minecraft:", "");
                        if (formula == "ore_drops")
                        {
                            if (lvl > 0) { int i = Random.Range(0, lvl + 2) - 1; if (i < 0) i = 0; s.Count *= i + 1; }
                        }
                        else if (formula == "uniform_bonus_count")
                            s.Count += Random.Range(0, (int)f["parameters"]["bonusMultiplier"] * lvl + 1);
                        else if (formula == "binomial_with_bonus_count")
                        {
                            int n = lvl + (int)f["parameters"]["extra"]; float p = (float)f["parameters"]["probability"];
                            for (int i = 0; i < n; i++) if (Random.value < p) s.Count++;
                        }
                        break;
                    }
                    case "explosion_decay":
                        if (ctx.Explosion) { int keep = 0; for (int i = 0; i < s.Count; i++) if (Random.value < 0.25f) keep++; s.Count = keep; }
                        break;
                    case "limit_count":
                    {
                        var lim = f["limit"];
                        if (lim.Type == JTokenType.Object)
                        {
                            if (lim["min"] != null) s.Count = Mathf.Max(s.Count, Mathf.FloorToInt(Number(lim["min"], 0)));
                            if (lim["max"] != null) s.Count = Mathf.Min(s.Count, Mathf.FloorToInt(Number(lim["max"], 64)));
                        }
                        else s.Count = Mathf.Min(s.Count, (int)lim);
                        break;
                    }
                    case "looting_enchant":
                        if (ctx.Looting > 0) s.Count += Mathf.RoundToInt(Random.Range(0f, ctx.Looting * Number(f["count"], 1)));
                        if (f["limit"] != null && (int)f["limit"] > 0) s.Count = Mathf.Min(s.Count, (int)f["limit"]);
                        break;
                    case "furnace_smelt":
                        if (ctx.OnFire)
                        {
                            var r = Crafting.Smelt("smelting", s);
                            if (r != null) { s.Item = r.Item; }
                        }
                        break;
                }
            }
        }

        /// <summary>A block's drops (PlayerEntity breaking it with `tool`), honouring "requires the correct tool".</summary>
        public static List<ItemStack> BlockDrops(BState s, ItemStack tool, bool explosion = false)
        {
            if (s.Block.ToolRequired && !Mining.IsSuitable(tool, s.Block)) return new List<ItemStack>();
            if (s.Block.Loot == null)
            {
                var it = s.Block.Item;
                return it != null && it.Id != "air" ? new List<ItemStack> { new ItemStack(it) } : new List<ItemStack>();
            }
            return Roll(s.Block.Loot, new Ctx { Tool = tool, State = s, Explosion = explosion });
        }

        /// <summary>Experience a block gives when mined (ExperienceDroppingBlock ranges), none with Silk Touch.</summary>
        public static int BlockXp(BlockDef b, ItemStack tool)
        {
            if (tool != null && tool.Level("silk_touch") > 0) return 0;
            switch (b.Id.Replace("deepslate_", ""))
            {
                case "coal_ore": return Random.Range(0, 3);
                case "diamond_ore": case "emerald_ore": return Random.Range(3, 8);
                case "lapis_ore": case "nether_quartz_ore": return Random.Range(2, 6);
                case "redstone_ore": return Random.Range(1, 6);
                case "nether_gold_ore": return Random.Range(0, 2);
                case "sculk": return 1;
                case "spawner": return Random.Range(15, 44);
            }
            return 0;
        }
    }
}
