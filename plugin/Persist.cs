using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// SubnautiCraft's save, one file per Subnautica save slot (BepInEx/plugins/SubnautiCraft/saves/slotXXXX.json),
    /// written whenever Subnautica saves so the two never disagree: inventory, armour, ender chest, game mode,
    /// health extras, hunger saturation, experience, effects, spawn point, game rules, every placed block and
    /// block entity, dropped items and Minecraft mobs.
    /// </summary>
    public static class Persist
    {
        public static bool Dirty;
        public static string Slot;
        public static Vector3? Spawn;

        private static string Dir => Path.Combine(Plugin.Dir, "saves");
        private static string FileFor(string slot) => Path.Combine(Dir, slot + ".json");

        public static void Save()
        {
            if (string.IsNullOrEmpty(Slot)) return;
            try
            {
                Directory.CreateDirectory(Dir);
                var o = new JObject
                {
                    ["version"] = 1,
                    ["mode"] = GameMode.Creative ? "creative" : "survival",
                    ["inventory"] = Inventory.Save(),
                    ["xp"] = new JObject { ["level"] = Xp.Level, ["progress"] = Xp.Progress, ["total"] = Xp.Total },
                    ["hunger"] = new JObject { ["saturation"] = Hunger.Saturation, ["exhaustion"] = Hunger.Exhaustion },
                    ["absorption"] = Health.Absorption,
                    ["effects"] = Effects.Save(),
                    ["rules"] = new JObject { ["keepInventory"] = Commands.KeepInventory, ["doMobSpawning"] = Commands.MobSpawning, ["naturalRegeneration"] = Commands.NaturalRegen },
                    ["blocks"] = BlockWorld.Save(),
                    ["perspective"] = View.I != null ? View.I.Perspective : 0,
                };
                if (Spawn.HasValue) o["spawn"] = new JArray(Spawn.Value.x, Spawn.Value.y, Spawn.Value.z);
                var items = new JArray();
                foreach (var e in ItemEntity.All) if (e != null && e.Stack != null && e.Stack.Count > 0) items.Add(new JObject { ["pos"] = new JArray(e.transform.position.x, e.transform.position.y, e.transform.position.z), ["stack"] = e.Stack.Save(), ["age"] = e.Age });
                o["items"] = items;
                var mobs = new JArray();
                foreach (var m in Mobs.All) if (m != null && !m.Dead && !m.Natural) mobs.Add(new JObject { ["type"] = m.Type, ["pos"] = new JArray(m.transform.position.x, m.transform.position.y, m.transform.position.z), ["hp"] = m.Health });
                o["mobs"] = mobs;
                var f = FileFor(Slot);
                File.WriteAllText(f + ".tmp", o.ToString(Newtonsoft.Json.Formatting.None));
                if (File.Exists(f)) { File.Copy(f, f + ".bak", true); File.Delete(f); }
                File.Move(f + ".tmp", f);
                Dirty = false;
                Plugin.Log.LogInfo("saved SubnautiCraft data for " + Slot + " (" + BlockWorld.Blocks.Count + " blocks)");
            }
            catch (System.Exception e) { Plugin.Log.LogError("SubnautiCraft save failed: " + e); }
        }

        public static void Load(string slot)
        {
            Slot = slot;
            Inventory.Clear();
            BlockWorld.Clear();
            ItemEntity.ClearAll();
            Mobs.ClearAll();
            Effects.Active.Clear();
            Xp.Level = 0; Xp.Progress = 0; Xp.Total = 0;
            Hunger.Saturation = 5f; Hunger.Exhaustion = 0;
            Spawn = null;
            GameModeUtils.GetGameMode(out var mode, out var cheats);
            bool snCreative = (mode & GameModeOption.NoSurvival) != 0 && (mode & GameModeOption.NoCost) != 0;
            GameMode.Creative = snCreative;
            var f = FileFor(slot);
            if (!File.Exists(f)) { FirstVisit(); Dirty = true; return; }
            try
            {
                var o = JObject.Parse(File.ReadAllText(f));
                GameMode.Creative = (string)o["mode"] == "creative";
                Inventory.Load(o["inventory"] as JObject);
                var xp = o["xp"];
                if (xp != null) { Xp.Level = (int)xp["level"]; Xp.Progress = (float)xp["progress"]; Xp.Total = (int)xp["total"]; }
                var h = o["hunger"];
                if (h != null) { Hunger.Saturation = (float)h["saturation"]; Hunger.Exhaustion = (float)h["exhaustion"]; }
                Health.Absorption = (float?)o["absorption"] ?? 0;
                Effects.Load(o["effects"]);
                var r = o["rules"];
                if (r != null) { Commands.KeepInventory = (bool)r["keepInventory"]; Commands.MobSpawning = (bool)r["doMobSpawning"]; Mobs.Spawning = Commands.MobSpawning; Commands.NaturalRegen = (bool?)r["naturalRegeneration"] ?? true; }
                if (o["spawn"] is JArray sp) Spawn = new Vector3((float)sp[0], (float)sp[1], (float)sp[2]);
                BlockWorld.Load(o["blocks"] as JObject);
                pendingPerspective = (int?)o["perspective"] ?? 0;
                if (o["items"] is JArray items)
                    foreach (var it in items)
                    {
                        var s = ItemStack.Load(it["stack"]);
                        var p = (JArray)it["pos"];
                        var e = ItemEntity.Spawn(s, new Vector3((float)p[0], (float)p[1], (float)p[2]), Vector3.zero, 0f);
                        if (e != null) e.Age = (int?)it["age"] ?? 0;
                    }
                if (o["mobs"] is JArray mobs)
                    foreach (var m in mobs)
                    {
                        var p = (JArray)m["pos"];
                        var mob = Mob.Create((string)m["type"], new Vector3((float)p[0], (float)p[1], (float)p[2]));
                        mob.Health = (float)m["hp"];
                    }
                Dirty = false;
                Plugin.Log.LogInfo("loaded SubnautiCraft data for " + slot + ": " + BlockWorld.Blocks.Count + " blocks");
            }
            catch (System.Exception e) { Plugin.Log.LogError("SubnautiCraft load failed (" + f + "): " + e); }
        }

        public static int pendingPerspective;

        /// <summary>
        /// First time in a save: Minecraft's survival start is an empty inventory; Subnautica's own starting items
        /// (from its lifepod or from the save) are brought in by SnBridge.
        /// </summary>
        private static void FirstVisit()
        {
            if (GameMode.Creative) return;
            Chat.Add("§e" + "SubnautiCraft : tu es Amai dans le monde de Subnautica. E = inventaire, T = chat, F5 = vue, F3+F4 = mode de jeu.");
        }
    }
}
