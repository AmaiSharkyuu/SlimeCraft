using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// SlimeCraft's save, one file per Slime Rancher game (BepInEx/plugins/SlimeCraft/saves/&lt;game&gt;.json), written
    /// whenever Slime Rancher saves so the two never disagree about where a slime is: inventory, armour, ender chest,
    /// slime storage, game mode, hunger, experience, effects, spawn point, game rules, every placed block and block
    /// entity, dropped items and Minecraft mobs. A SlimeCraft 0.1 save (&lt;game&gt;.txt) is imported the first time.
    /// </summary>
    public static class Persist
    {
        public static bool Dirty;
        public static string Slot;
        public static Vector3? Spawn;

        private static string Dir => Path.Combine(Plugin.Dir, "saves");
        private static string FileFor(string slot)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) slot = slot.Replace(c, '_');
            return Path.Combine(Dir, slot + ".json");
        }

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
                    ["hunger"] = new JObject { ["food"] = Hunger.FoodLevel, ["saturation"] = Hunger.Saturation, ["exhaustion"] = Hunger.Exhaustion },
                    ["absorption"] = Health.Absorption,
                    ["effects"] = Effects.Save(),
                    ["rules"] = new JObject { ["keepInventory"] = Commands.KeepInventory, ["doMobSpawning"] = Commands.MobSpawning, ["naturalRegeneration"] = Commands.NaturalRegen },
                    ["blocks"] = BlockWorld.Save(),
                    ["carve"] = SrCarve.Save(),
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
                Plugin.Log.LogInfo("saved SlimeCraft data for " + Slot + " (" + BlockWorld.Blocks.Count + " blocks)");
            }
            catch (System.Exception e) { Plugin.Log.LogError("SlimeCraft save failed: " + e); }
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
            Hunger.FoodLevel = 20; Hunger.Saturation = 5f; Hunger.Exhaustion = 0;
            Spawn = null;
            GameMode.Creative = false;
            var f = FileFor(slot);
            if (!File.Exists(f))
            {
                var v1 = Path.ChangeExtension(f, ".txt");
                if (File.Exists(v1)) ImportV1(v1);
                else FirstVisit();
                Dirty = true;
                return;
            }
            try
            {
                var o = JObject.Parse(File.ReadAllText(f));
                GameMode.Creative = (string)o["mode"] == "creative";
                Inventory.Load(o["inventory"] as JObject);
                var xp = o["xp"];
                if (xp != null) { Xp.Level = (int)xp["level"]; Xp.Progress = (float)xp["progress"]; Xp.Total = (int)xp["total"]; }
                var h = o["hunger"];
                if (h != null) { Hunger.FoodLevel = (int?)h["food"] ?? 20; Hunger.Saturation = (float)h["saturation"]; Hunger.Exhaustion = (float)h["exhaustion"]; }
                Health.Absorption = (float?)o["absorption"] ?? 0;
                Effects.Load(o["effects"]);
                var r = o["rules"];
                if (r != null) { Commands.KeepInventory = (bool)r["keepInventory"]; Commands.MobSpawning = (bool)r["doMobSpawning"]; Mobs.Spawning = Commands.MobSpawning; Commands.NaturalRegen = (bool?)r["naturalRegeneration"] ?? true; }
                if (o["spawn"] is JArray sp) Spawn = new Vector3((float)sp[0], (float)sp[1], (float)sp[2]);
                BlockWorld.Load(o["blocks"] as JObject);
                SrCarve.Load(o["carve"] as JObject);
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
                Plugin.Log.LogInfo("loaded SlimeCraft data for " + slot + ": " + BlockWorld.Blocks.Count + " blocks");
            }
            catch (System.Exception e) { Plugin.Log.LogError("SlimeCraft load failed (" + f + "): " + e); }
        }

        public static int pendingPerspective;

        /// <summary>First time in a game: a word about the controls in the chat.</summary>
        private static void FirstVisit()
        {
            Chat.Add("§e" + McData.ModText("welcome"));
        }

        // ------------------------------------------------------------------------------------------------ SlimeCraft 0.1

        /// <summary>
        /// SlimeCraft 0.1 kept a text file per game: "slot i b:id:n" / "a:SLIME_ID:n" lines, the slime storage, and
        /// "x y z id rot [contents]" blocks. Everything comes over: the items (with shulker contents), the slimes, the
        /// blocks in their default state turned the way they were, chests and shulker boxes with what they held.
        /// </summary>
        private static void ImportV1(string path)
        {
            ItemStack Item(string t)
            {
                if (string.IsNullOrEmpty(t) || t == "-") return null;
                string inner = null;
                int plus = t.IndexOf('+');
                if (plus >= 0) { inner = t.Substring(plus + 1); t = t.Substring(0, plus); }
                var f = t.Split(':');
                if (f.Length < 3) return null;
                int n = int.Parse(f[2]);
                ItemStack r = null;
                if (f[0] == "b") r = ItemStack.Of(f[1], n);
                else if (SrCreatures.TryGet(f[1], out var id)) r = new ItemStack(id, n);
                if (r != null && inner != null)
                {
                    var parts = inner.Split('/');
                    r.Contents = new ItemStack[27];
                    for (int i = 0; i < parts.Length && i < 27; i++) r.Contents[i] = Item(parts[i]);
                }
                return r;
            }
            ItemStack[] List(string t, int size)
            {
                var r = new ItemStack[size];
                var parts = t.Split(',');
                for (int i = 0; i < parts.Length && i < size; i++) r[i] = Item(parts[i]);
                return r;
            }
            string[] facing = { "north", "east", "south", "west" }; // SlimeCraft 0.1 turned fronts by quarter turns from +z
            int blocks = 0;
            bool inBlocks = false;
            foreach (var line in File.ReadAllLines(path))
            {
                if (line == "[inventory]") { inBlocks = false; continue; }
                if (line == "[blocks]") { inBlocks = true; continue; }
                var f = line.Split(' ');
                try
                {
                    if (!inBlocks)
                    {
                        if (f[0] == "mode") GameMode.Creative = f[1] == "creative";
                        else if (f[0] == "sel") Inventory.Selected = Mathf.Clamp(int.Parse(f[1]), 0, 8);
                        else if (f[0] == "slot") Inventory.Main[int.Parse(f[1])] = Item(f[2]);
                        else if (f[0] == "storage") { var l = List(f[1], Inventory.SlimeStorage.Length); for (int i = 0; i < l.Length; i++) Inventory.SlimeStorage[i] = l[i]; }
                        continue;
                    }
                    if (f.Length < 5) continue;
                    var def = McData.Block(f[3]);
                    if (def == null) continue;
                    var cell = new Vector3Int(int.Parse(f[0]), int.Parse(f[1]), int.Parse(f[2]));
                    int rot = int.Parse(f[4]);
                    var st = def.Default;
                    if (def.HasProp("type") && f[3].EndsWith("_slab")) st = st.With("type", rot == 1 ? "top" : rot == 2 ? "double" : "bottom");
                    else if (def.HasProp("axis")) st = st.With("axis", rot == 1 ? "x" : rot == 2 ? "z" : "y");
                    else if (def.HasProp("half") && f[3].EndsWith("_stairs")) st = st.With("facing", facing[rot & 3]).With("half", rot >= 4 ? "top" : "bottom");
                    else if (def.HasProp("facing")) st = st.With("facing", facing[rot & 3]);
                    BlockWorld.Set(cell, st, false);
                    blocks++;
                    if (f.Length > 5) BlockWorld.Entity(cell, true, 27).Items = List(f[5], 27);
                }
                catch (System.Exception e) { Plugin.Log.LogWarning("SlimeCraft 0.1 line skipped (" + line + "): " + e.Message); }
            }
            Plugin.Log.LogInfo("imported SlimeCraft 0.1 data: " + blocks + " blocks");
        }
    }
}
