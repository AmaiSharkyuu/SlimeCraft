using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Minecraft's chat (T, or / to start a command): messages fading after 10 s, the full history while open,
    /// sent-message history with the arrow keys, and Brigadier-style suggestions with Tab completion for every
    /// command, item, block, effect, enchantment and entity - Minecraft's aquatic mobs and Subnautica's creatures.
    /// Anything that is not a Minecraft command goes to Subnautica's own console (item, spawn, warp, fastbuild...).
    /// </summary>
    public static class Chat
    {
        private struct Line { public string Text; public float Time; }
        private static readonly List<Line> lines = new List<Line>();
        private static readonly List<string> sent = new List<string>();
        public static bool Open;
        private static string input = "";
        private static int caret, histIndex = -1, scroll;
        private static List<string> suggestions = new List<string>();
        private static int sugIndex = -1, sugStart;
        private static string sugBase;
        private static int openedFrame;
        public static string PlayerName = "Amai";

        public static void System(string text) => Add(text);
        public static void ActionBar(string text) => Hud.ActionBar(text);

        public static void Add(string text)
        {
            foreach (var part in text.Split('\n')) lines.Add(new Line { Text = part, Time = Time.unscaledTime });
            if (lines.Count > 100) lines.RemoveRange(0, lines.Count - 100);
            Plugin.Log.LogInfo("[chat] " + text);
        }

        public static void OpenChat(string prefix)
        {
            Open = true; input = prefix; caret = input.Length; histIndex = -1; scroll = 0; openedFrame = Time.frameCount;
            UWE.Utils.lockCursor = false;
            UpdateSuggestions();
        }

        public static void Close()
        {
            Open = false; input = ""; suggestions.Clear();
            UWE.Utils.lockCursor = true;
        }

        /// <summary>Text input runs in OnGUI so characters come with the right keyboard layout.</summary>
        public static void HandleEvent(Event e)
        {
            if (!Open || e.type != EventType.KeyDown) return;
            if (Time.frameCount == openedFrame && (e.character == 't' || e.character == 'T' || e.character == '/' && input == "/")) { e.Use(); return; }
            switch (e.keyCode)
            {
                case KeyCode.Escape: Close(); e.Use(); return;
                case KeyCode.Return: case KeyCode.KeypadEnter: Submit(); e.Use(); return;
                case KeyCode.Backspace: if (caret > 0) { input = input.Remove(caret - 1, 1); caret--; UpdateSuggestions(); } e.Use(); return;
                case KeyCode.Delete: if (caret < input.Length) { input = input.Remove(caret, 1); UpdateSuggestions(); } e.Use(); return;
                case KeyCode.LeftArrow: caret = Mathf.Max(0, caret - 1); e.Use(); return;
                case KeyCode.RightArrow: caret = Mathf.Min(input.Length, caret + 1); e.Use(); return;
                case KeyCode.Home: caret = 0; e.Use(); return;
                case KeyCode.End: caret = input.Length; e.Use(); return;
                case KeyCode.Tab: Complete(e.shift ? -1 : 1); e.Use(); return;
                case KeyCode.UpArrow:
                    if (suggestions.Count > 0 && sugIndex >= 0) { Complete(-1); e.Use(); return; }
                    if (sent.Count > 0) { histIndex = histIndex < 0 ? sent.Count - 1 : Mathf.Max(0, histIndex - 1); input = sent[histIndex]; caret = input.Length; UpdateSuggestions(); }
                    e.Use(); return;
                case KeyCode.DownArrow:
                    if (suggestions.Count > 0 && sugIndex >= 0) { Complete(1); e.Use(); return; }
                    if (histIndex >= 0) { histIndex++; if (histIndex >= sent.Count) { histIndex = -1; input = ""; } else input = sent[histIndex]; caret = input.Length; UpdateSuggestions(); }
                    e.Use(); return;
                case KeyCode.PageUp: scroll = Mathf.Min(scroll + 7, Mathf.Max(0, lines.Count - 10)); e.Use(); return;
                case KeyCode.PageDown: scroll = Mathf.Max(0, scroll - 7); e.Use(); return;
            }
            if (e.character >= ' ' && input.Length < 256)
            {
                input = input.Insert(caret, e.character.ToString());
                caret++;
                UpdateSuggestions();
                e.Use();
            }
        }

        private static void Submit()
        {
            var text = input.Trim();
            Close();
            if (text.Length == 0) return;
            if (sent.Count == 0 || sent[sent.Count - 1] != text) sent.Add(text);
            if (text.StartsWith("/")) Commands.Run(text.Substring(1));
            else Add("<" + PlayerName + "> " + text);
        }

        // ------------------------------------------------------------------------------------------------ suggestions

        private static void UpdateSuggestions()
        {
            sugIndex = -1;
            suggestions.Clear();
            if (!input.StartsWith("/")) return;
            var before = input.Substring(1, caret - 1);
            var args = before.Split(' ');
            int argIndex = args.Length - 1;
            var current = args[argIndex];
            sugStart = caret - current.Length;
            sugBase = current;
            var cands = Commands.Suggest(args, argIndex);
            var low = current.ToLowerInvariant();
            foreach (var c in cands)
            {
                var cl = c.ToLowerInvariant();
                if (cl.StartsWith(low) || cl.StartsWith("minecraft:" + low) || cl.Contains(":" + low) || low.Length >= 2 && cl.Contains("_" + low)) suggestions.Add(c);
                if (suggestions.Count > 200) break;
            }
            suggestions.Sort((a, b) => { bool sa = a.ToLowerInvariant().StartsWith(low), sb = b.ToLowerInvariant().StartsWith(low); return sa != sb ? (sa ? -1 : 1) : string.CompareOrdinal(a, b); });
        }

        private static void Complete(int dir)
        {
            if (suggestions.Count == 0) return;
            sugIndex = (sugIndex + dir + suggestions.Count) % suggestions.Count;
            var s = suggestions[sugIndex];
            input = input.Substring(0, sugStart) + s + input.Substring(caret);
            caret = sugStart + s.Length;
        }

        // ------------------------------------------------------------------------------------------------ drawing

        public static void DrawMessages()
        {
            int h = McGui.ScaledHeight;
            int baseY = h - 48;
            int max = Open ? 20 : 10;
            int y = baseY;
            int shown = 0;
            for (int i = lines.Count - 1 - (Open ? scroll : 0); i >= 0 && shown < max; i--)
            {
                float age = Time.unscaledTime - lines[i].Time;
                float a = Open ? 1f : Mathf.Clamp01((10f - age) / 1f);
                if (a <= 0) break;
                McGui.Fill(0, y - 1, 320, 9, new Color(0, 0, 0, 0.5f * a));
                McFont.Draw(lines[i].Text, 2, y, new Color(1, 1, 1, a));
                y -= 9;
                shown++;
            }
        }

        public static void DrawInput()
        {
            if (!Open) return;
            int w = McGui.ScaledWidth, h = McGui.ScaledHeight;
            McGui.Fill(2, h - 14, w - 4, 12, new Color(0, 0, 0, 0.5f));
            McFont.Draw(input, 4, h - 12, Color.white);
            if ((int)(Time.unscaledTime * 3) % 2 == 0)
            {
                float cx = 4 + McFont.Width(input.Substring(0, caret));
                if (caret == input.Length) McFont.Draw("_", cx, h - 12, Color.white);
                else McGui.Fill(cx, h - 13, 1, 10, new Color(0.82f, 0.82f, 0.82f));
            }
            // ghost of the first suggestion, and the list (ChatInputSuggestor)
            if (suggestions.Count > 0)
            {
                var first = sugIndex >= 0 ? null : suggestions[0];
                if (first != null && first.StartsWith(sugBase) && caret == input.Length) McFont.Draw("§7" + first.Substring(sugBase.Length), 4 + McFont.Width(input), h - 12, Color.white);
                int count = Mathf.Min(10, suggestions.Count);
                int start = sugIndex >= 10 ? sugIndex - 9 : 0;
                float bw = 0;
                for (int i = start; i < start + count && i < suggestions.Count; i++) bw = Mathf.Max(bw, McFont.Width(suggestions[i]));
                float x = 4 + McFont.Width(input.Substring(0, sugStart));
                float y = h - 14 - count * 12;
                McGui.Fill(x - 1, y, bw + 2, count * 12, new Color(0, 0, 0, 0.82f));
                for (int i = start, k = 0; i < start + count && i < suggestions.Count; i++, k++)
                    McFont.Draw(suggestions[i], x, y + 2 + k * 12, i == sugIndex ? new Color(1, 1, 0.33f) : new Color(0.67f, 0.67f, 0.67f));
            }
        }
    }

    /// <summary>The commands. Coordinates are Minecraft's (z points south = Unity -z), ~ is relative.</summary>
    public static class Commands
    {
        private static readonly string[] Names = { "gamemode", "give", "enchant", "effect", "tp", "teleport", "time", "weather", "kill", "clear", "xp", "experience", "summon", "setblock", "fill", "gamerule", "difficulty", "spawnpoint", "seed", "help", "say", "me", "title", "playsound", "heal", "feed", "sn", "locate", "msg", "tell" };
        private static List<string> itemIds, blockIds, entityIds;
        public static bool KeepInventory, MobSpawning = true, DaylightCycle = true, NaturalRegen = true;

        private static List<string> ItemIds()
        {
            if (itemIds != null) return itemIds;
            itemIds = new List<string>();
            foreach (var d in McData.ItemList) itemIds.Add("minecraft:" + d.Id);
            foreach (TechType t in global::System.Enum.GetValues(typeof(TechType))) if (t != TechType.None) itemIds.Add("subnautica:" + t.ToString().ToLowerInvariant());
            itemIds = itemIds.Distinct().ToList();
            return itemIds;
        }

        private static List<string> BlockIds()
        {
            if (blockIds != null) return blockIds;
            blockIds = McData.Blocks.Keys.Select(k => "minecraft:" + k).ToList();
            return blockIds;
        }

        private static List<string> EntityIds()
        {
            if (entityIds != null) return entityIds;
            entityIds = Mobs.Aquatic.Select(a => "minecraft:" + a).ToList();
            foreach (var n in SnCreatures.Names) entityIds.Add("subnautica:" + n);
            return entityIds;
        }

        private static readonly string[] Targets = { "@s", "@p", "@a", "Amai" };

        public static IEnumerable<string> Suggest(string[] args, int i)
        {
            if (i == 0) return Names.Select(n => n);
            switch (args[0])
            {
                case "gamemode": return i == 1 ? new[] { "survival", "creative", "adventure", "spectator" } : Targets;
                case "give": return i == 1 ? Targets : i == 2 ? ItemIds() : new[] { "1", "16", "64" };
                case "enchant": return i == 1 ? Targets : i == 2 ? McData.Enchants.Keys.Select(k => "minecraft:" + k) : new[] { "1", "2", "3", "4", "5" };
                case "effect":
                    if (i == 1) return new[] { "give", "clear" };
                    if (i == 2) return Targets;
                    if (i == 3) return McData.Effects.Keys.Select(k => "minecraft:" + k);
                    return i == 4 ? new[] { "30", "60", "600", "infinite" } : i == 5 ? new[] { "0", "1", "2", "4" } : new[] { "true", "false" };
                case "tp": case "teleport": return i == 1 ? Targets.Concat(new[] { "~" }) : new[] { "~", "~ ~", "~ ~ ~" };
                case "time": return i == 1 ? new[] { "set", "add", "query" } : args[1] == "query" ? new[] { "daytime", "gametime", "day" } : new[] { "day", "noon", "night", "midnight", "1000", "13000" };
                case "weather": return new[] { "clear", "rain", "thunder" };
                case "kill": return Targets.Concat(new[] { "@e", "@e[type=minecraft:drowned]", "@e[type=minecraft:cod]" });
                case "clear": return i == 1 ? Targets : ItemIds();
                case "xp": case "experience": return i == 1 ? new[] { "add", "set", "query" } : i == 2 ? Targets : i == 3 ? new[] { "10", "100", "1000" } : new[] { "points", "levels" };
                case "summon": return i == 1 ? EntityIds() : new[] { "~", "~ ~", "~ ~ ~" };
                case "setblock": return i <= 3 ? new[] { "~" } : i == 4 ? BlockIds() : new[] { "replace", "destroy", "keep" };
                case "fill": return i <= 6 ? new[] { "~" } : i == 7 ? BlockIds() : new[] { "replace", "destroy", "hollow", "outline", "keep" };
                case "gamerule": return i == 1 ? new[] { "keepInventory", "doMobSpawning", "doDaylightCycle", "naturalRegeneration" } : new[] { "true", "false" };
                case "difficulty": return new[] { "peaceful", "easy", "normal", "hard" };
                case "title": return i == 1 ? Targets : i == 2 ? new[] { "title", "subtitle", "actionbar", "clear" } : new string[0];
                case "playsound": return i == 1 ? McData.SoundEvents.Keys.Select(k => "minecraft:" + k) : Targets;
                case "help": return Names;
                case "sn": return new[] { "item", "spawn", "warp", "fastbuild", "fastgrow", "nodamage", "oxygen", "nocost", "unlockall", "daynight", "biome", "goto" };
            }
            return new string[0];
        }

        public static void Run(string line)
        {
            var a = line.Split(new[] { ' ' }, global::System.StringSplitOptions.RemoveEmptyEntries);
            if (a.Length == 0) return;
            try { Exec(a, line); }
            catch (global::System.Exception e) { Chat.Add("§c" + McData.T("command.failed", "Une erreur inattendue s'est produite lors de l'exécution de cette commande")); Plugin.Log.LogError(e); }
        }

        private static string Strip(string id) => id.Replace("minecraft:", "");

        private static Vector3 Feet() => Player.main.transform.position - Vector3.up * Motor.EyeHeight;

        /// <summary>Minecraft coordinates (x, y, z with z pointing south, ~ relative to the player) to a Unity point.</summary>
        private static Vector3 Coords(string[] a, int i)
        {
            var f = Feet();
            float X(string s, float rel) => s.StartsWith("~") ? rel + (s.Length > 1 ? float.Parse(s.Substring(1), CultureInfo.InvariantCulture) : 0) : float.Parse(s, CultureInfo.InvariantCulture) + (s.Contains(".") ? 0 : 0.5f);
            float x = X(a[i], f.x), y = a[i + 1].StartsWith("~") ? X(a[i + 1], f.y) : float.Parse(a[i + 1], CultureInfo.InvariantCulture);
            float mcZ = X(a[i + 2], -f.z);
            return new Vector3(x, y, -mcZ);
        }

        private static ItemStack ParseItem(string id, int count)
        {
            id = id.ToLowerInvariant();
            if (id.StartsWith("subnautica:") || id.StartsWith("sn:"))
            {
                var name = id.Substring(id.IndexOf(':') + 1);
                if (SnCreatures.TryGet(name, out var tt)) return new ItemStack(tt, count);
                return null;
            }
            var d = McData.Item(Strip(id));
            if (d != null) return new ItemStack(d, count);
            if (SnCreatures.TryGet(id, out var t2)) return new ItemStack(t2, count);
            return null;
        }

        private static void Exec(string[] a, string line)
        {
            switch (a[0])
            {
                case "gamemode":
                {
                    if (a.Length < 2) { Usage("/gamemode <mode>"); return; }
                    bool creative = a[1] == "creative" || a[1] == "1" || a[1] == "c" || a[1] == "spectator" || a[1] == "3";
                    GameMode.Set(creative);
                    return;
                }
                case "give":
                {
                    if (a.Length < 3) { Usage("/give <cible> <objet> [nombre]"); return; }
                    int n = a.Length > 3 ? int.Parse(a[3]) : 1;
                    var s = ParseItem(a[2], n);
                    if (s == null) { Chat.Add("§c" + McData.Tf("argument.item.id.invalid", a[2])); return; }
                    if (s.IsSn && !SnBridge.IsVirtual(s.Tech)) SnBridge.GiveReal(s.Tech, n);
                    else
                    {
                        int left = n;
                        while (left > 0) { var part = s.Copy(Mathf.Min(left, s.MaxStack)); left -= part.Count; int took = Inventory.Add(part); if (took < part.Count) ItemEntity.Spawn(part.Copy(part.Count - took), Player.main.transform.position, Vector3.zero, 0); }
                    }
                    Sounds.Play("entity.item.pickup", null, 0.2f, ((Random.value - Random.value) * 0.7f + 1f) * 2f);
                    Chat.Add(McData.Tf("commands.give.success.single", n, "[" + s.Name + "]", Chat.PlayerName));
                    return;
                }
                case "enchant":
                {
                    if (a.Length < 3) { Usage("/enchant <cible> <enchantement> [niveau]"); return; }
                    var h = Inventory.Held;
                    var id = Strip(a[2]);
                    if (h == null || !h.IsMc || !McData.Enchants.TryGetValue(id, out var e)) { Chat.Add("§c" + McData.T("commands.enchant.failed")); return; }
                    int lvl = a.Length > 3 ? int.Parse(a[3]) : 1;
                    if (lvl > e.Max) { Chat.Add("§c" + McData.Tf("commands.enchant.failed.level", lvl, e.Max)); return; }
                    if (!e.Items.Contains(h.Item.Id) && !h.Is("book") && !h.Is("enchanted_book")) { Chat.Add("§c" + McData.Tf("commands.enchant.failed.incompatible", h.Name)); return; }
                    if (h.Ench == null) h.Ench = new Dictionary<string, int>();
                    h.Ench[id] = lvl;
                    Chat.Add(McData.Tf("commands.enchant.success.single", e.Name + " " + Tooltip.Roman(lvl), Chat.PlayerName));
                    return;
                }
                case "effect":
                {
                    if (a.Length >= 2 && a[1] == "clear")
                    {
                        if (a.Length >= 4) Effects.Remove(Strip(a[3])); else Effects.Clear();
                        Chat.Add(McData.Tf("commands.effect.clear.everything.success.single", Chat.PlayerName));
                        return;
                    }
                    if (a.Length < 4) { Usage("/effect give <cible> <effet> [secondes] [amplificateur]"); return; }
                    var id = Strip(a[3]);
                    if (!McData.Effects.ContainsKey(id)) { Chat.Add("§c" + McData.Tf("effect.effectNotFound", a[3])); return; }
                    int secs = a.Length > 4 ? (a[4] == "infinite" ? 1000000 : int.Parse(a[4])) : 30;
                    int amp = a.Length > 5 ? int.Parse(a[5]) : 0;
                    Effects.Add(id, secs >= 1000000 ? 2000000000 : secs * 20, amp);
                    Chat.Add(McData.Tf("commands.effect.give.success.single", McData.Effects[id].Name, Chat.PlayerName));
                    return;
                }
                case "tp": case "teleport":
                {
                    int i = a.Length >= 5 ? 2 : 1;
                    if (a.Length < i + 3) { Usage("/tp <x> <y> <z>"); return; }
                    var to = Coords(a, i);
                    Player.main.SetPosition(to + Vector3.up * Motor.EyeHeight);
                    Motor.Vel = Vector3.zero; Motor.FallDistance = 0;
                    Chat.Add(McData.Tf("commands.teleport.success.location.single", Chat.PlayerName, a[i], a[i + 1], a[i + 2]));
                    return;
                }
                case "time":
                {
                    var dnc = DayNightCycle.main;
                    if (a.Length >= 2 && a[1] == "query")
                    {
                        // Minecraft day ticks: 0 = sunrise (06:00); Subnautica's day scalar 0.25 = sunrise
                        int t = Mathf.RoundToInt(Mathf.Repeat(dnc.GetDayScalar() - 0.25f, 1f) * 24000f);
                        Chat.Add(McData.Tf("commands.time.query", t));
                        return;
                    }
                    if (a.Length < 3) { Usage("/time set <temps>"); return; }
                    int ticks;
                    switch (a[2]) { case "day": ticks = 1000; break; case "noon": ticks = 6000; break; case "night": ticks = 13000; break; case "midnight": ticks = 18000; break; default: ticks = int.Parse(a[2].TrimEnd('t')); break; }
                    float target = Mathf.Repeat(ticks / 24000f + 0.25f, 1f);
                    float now = dnc.GetDayScalar();
                    float delta = a[1] == "add" ? ticks / 24000f : Mathf.Repeat(target - now, 1f);
                    dnc.timePassedAsDouble += delta * 1200f;
                    Chat.Add(McData.Tf("commands.time.set", ticks));
                    return;
                }
                case "weather": Chat.Add(McData.T("commands.weather.set.clear")); return;
                case "kill":
                {
                    if (a.Length >= 2 && a[1].StartsWith("@e"))
                    {
                        string type = null;
                        var m = global::System.Text.RegularExpressions.Regex.Match(a[1], @"type=(?:minecraft:)?(\w+)");
                        if (m.Success) type = m.Groups[1].Value;
                        int n = 0;
                        foreach (var mob in Mobs.All.ToArray()) if (type == null || mob.Type == type) { mob.Damage(1e6f, "kill", 0, Vector3.zero); n++; }
                        if (type == null || type == "item") foreach (var it in ItemEntity.All.ToArray()) { Object.Destroy(it.gameObject); n++; }
                        Chat.Add(McData.Tf("commands.kill.success.multiple", n));
                        return;
                    }
                    Health.Damage(1e6f, "kill");
                    Chat.Add(McData.Tf("commands.kill.success.single", Chat.PlayerName));
                    return;
                }
                case "clear":
                {
                    int n = 0;
                    var filter = a.Length >= 3 ? ParseItem(a[2], 1) : null;
                    for (int i = 0; i < 36; i++) if (Inventory.Main[i] != null && (filter == null || Inventory.Main[i].Id == filter.Id)) { n += Inventory.Main[i].Count; if (Inventory.Main[i].IsSn) SnBridge.Consume(Inventory.Main[i].Tech, Inventory.Main[i].Count); Inventory.Main[i] = null; }
                    if (filter == null) { for (int i = 0; i < 4; i++) { if (Inventory.Armor[i] != null) n++; Inventory.Armor[i] = null; } if (Inventory.Offhand != null) n += Inventory.Offhand.Count; Inventory.Offhand = null; }
                    Chat.Add(McData.Tf("commands.clear.success.single", n, Chat.PlayerName));
                    return;
                }
                case "xp": case "experience":
                {
                    if (a.Length >= 2 && a[1] == "query") { Chat.Add(McData.Tf("commands.experience.query.levels", Chat.PlayerName, Xp.Level)); return; }
                    if (a.Length < 4) { Usage("/xp add <cible> <nombre> [points|levels]"); return; }
                    int n = int.Parse(a[3]);
                    bool levels = a.Length > 4 && a[4] == "levels";
                    if (a[1] == "set") { if (levels) { Xp.Level = n; } else { Xp.Level = 0; Xp.Progress = 0; Xp.Add(n); } }
                    else if (levels) Xp.AddLevels(n); else Xp.Add(n);
                    Chat.Add(McData.Tf(levels ? "commands.experience.add.levels.success.single" : "commands.experience.add.points.success.single", n, Chat.PlayerName));
                    return;
                }
                case "summon":
                {
                    if (a.Length < 2) { Usage("/summon <entité> [x y z]"); return; }
                    var type = a[1].ToLowerInvariant().Replace("subnautica:", "").Replace("minecraft:", "");
                    var at = a.Length >= 5 ? Coords(a, 2) : Feet() + MainCamera.camera.transform.forward * 3f + Vector3.up;
                    if (Mobs.Summon(type, at)) Chat.Add(McData.Tf("commands.summon.success", type));
                    else Chat.Add("§c" + McData.Tf("argument.entity.notfound.entity", type));
                    return;
                }
                case "setblock":
                {
                    if (a.Length < 5) { Usage("/setblock <x> <y> <z> <bloc>"); return; }
                    var c = BlockWorld.CellAt(Coords(a, 1));
                    var s = ParseBlock(a[4]);
                    if (s == null && Strip(a[4]) != "air") { Chat.Add("§c" + McData.Tf("argument.block.id.invalid", a[4])); return; }
                    if (a.Length > 5 && a[5] == "keep" && BlockWorld.Has(c)) return;
                    if (a.Length > 5 && a[5] == "destroy" && BlockWorld.Has(c)) Interaction.BreakBlock(c, null, true);
                    BlockWorld.Set(c, s);
                    if (s != null) BlockRules.PlaceCompanion(c, s);
                    Chat.Add(McData.Tf("commands.setblock.success", a[1], a[2], a[3]));
                    return;
                }
                case "fill":
                {
                    if (a.Length < 8) { Usage("/fill <x1> <y1> <z1> <x2> <y2> <z2> <bloc> [mode]"); return; }
                    var c1 = BlockWorld.CellAt(Coords(a, 1)); var c2 = BlockWorld.CellAt(Coords(a, 4));
                    var s = ParseBlock(a[7]);
                    string mode = a.Length > 8 ? a[8] : "replace";
                    var min = Vector3Int.Min(c1, c2); var max = Vector3Int.Max(c1, c2);
                    long vol = (long)(max.x - min.x + 1) * (max.y - min.y + 1) * (max.z - min.z + 1);
                    if (vol > 32768) { Chat.Add("§c" + McData.Tf("commands.fill.toobig", 32768, vol)); return; }
                    int n = 0;
                    for (int x = min.x; x <= max.x; x++) for (int y = min.y; y <= max.y; y++) for (int z = min.z; z <= max.z; z++)
                    {
                        var c = new Vector3Int(x, y, z);
                        bool edge = x == min.x || x == max.x || y == min.y || y == max.y || z == min.z || z == max.z;
                        if (mode == "outline" && !edge) continue;
                        if (mode == "keep" && BlockWorld.Has(c)) continue;
                        if (mode == "destroy" && BlockWorld.Has(c)) Interaction.BreakBlock(c, null, true);
                        BlockWorld.Set(c, mode == "hollow" && !edge ? null : s, false);
                        n++;
                    }
                    Chat.Add(McData.Tf("commands.fill.success", n));
                    return;
                }
                case "gamerule":
                {
                    if (a.Length < 3) { Usage("/gamerule <règle> <valeur>"); return; }
                    bool v = a[2] == "true";
                    switch (a[1]) { case "keepInventory": KeepInventory = v; break; case "doMobSpawning": MobSpawning = v; Mobs.Spawning = v; break; case "doDaylightCycle": DaylightCycle = v; if (v) DayNightCycle.main.Resume(); else DayNightCycle.main.Pause(); break; case "naturalRegeneration": NaturalRegen = v; break; }
                    Persist.Dirty = true;
                    Chat.Add(McData.Tf("commands.gamerule.set", a[1], a[2]));
                    return;
                }
                case "difficulty": Chat.Add(McData.Tf("commands.difficulty.success", a.Length > 1 ? a[1] : "normal")); return;
                case "spawnpoint": Persist.Spawn = Feet() + Vector3.up * Motor.EyeHeight; Chat.Add(McData.Tf("commands.spawnpoint.success.single", Mathf.FloorToInt(Feet().x), Mathf.FloorToInt(Feet().y), Mathf.FloorToInt(-Feet().z), 0, Chat.PlayerName)); return;
                case "seed": Chat.Add(McData.Tf("commands.seed.success", "[" + Persist.Slot + "]")); return;
                case "say": Chat.Add("[" + Chat.PlayerName + "] " + line.Substring(4)); return;
                case "me": Chat.Add("* " + Chat.PlayerName + " " + line.Substring(3)); return;
                case "msg": case "tell": Chat.Add("§7§o" + McData.Tf("commands.message.display.outgoing", a.Length > 1 ? a[1] : "", a.Length > 2 ? string.Join(" ", a.Skip(2)) : "")); return;
                case "title":
                {
                    if (a.Length < 3) return;
                    var text = a.Length > 3 ? string.Join(" ", a.Skip(3)).Trim('"') : "";
                    if (a[2] == "title") Hud.Title(text, null);
                    else if (a[2] == "subtitle") Hud.Title(null, text);
                    else if (a[2] == "actionbar") Hud.ActionBar(text);
                    return;
                }
                case "playsound": if (a.Length > 1) Sounds.Play(Strip(a[1]), null); return;
                case "heal": Health.Heal(20); return;
                case "feed": Hunger.Eat(20, 1f); Hunger.Drink(100f); return;
                case "locate": Chat.Add("§c" + McData.T("commands.locate.structure.not_found", "Aucune structure de ce type à proximité")); return;
                case "help":
                    Chat.Add("§e--- " + McData.T("commands.help.header", "Aide") + " ---");
                    foreach (var n in Names) Chat.Add("/" + n);
                    Chat.Add("§7(Les autres commandes vont à la console de Subnautica : item, spawn, warp, fastbuild...)");
                    return;
                case "sn":
                    DevConsole.SendConsoleCommand(string.Join(" ", a.Skip(1)));
                    return;
            }
            // not Minecraft's: Subnautica's console
            if (DevConsole.SendConsoleCommand(line)) return;
            Chat.Add("§c" + McData.T("command.unknown.command", "Commande inconnue ou incomplète, voir l'erreur ci-dessous"));
            Chat.Add("§c§n" + line + "§r§c§o<--[ICI]");
        }

        private static void Usage(string u) => Chat.Add("§c" + u);

        /// <summary>"minecraft:oak_stairs[facing=east,half=top]".</summary>
        public static BState ParseBlock(string s)
        {
            s = Strip(s);
            string props = null;
            int br = s.IndexOf('[');
            if (br >= 0) { props = s.Substring(br + 1).TrimEnd(']'); s = s.Substring(0, br); }
            var b = McData.Block(s);
            if (b == null || b.Id == "air") return null;
            var st = b.Default;
            if (props != null)
                foreach (var kv in props.Split(','))
                {
                    var p = kv.Split('=');
                    if (p.Length == 2) st = st.With(p[0].Trim(), p[1].Trim());
                }
            return st;
        }
    }
}
