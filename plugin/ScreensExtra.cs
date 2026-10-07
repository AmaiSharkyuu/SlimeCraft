using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>java.util.Random, so enchantment offers come out exactly as Minecraft's seeded ones.</summary>
    public class JavaRandom
    {
        private long seed;
        public JavaRandom(long s) { SetSeed(s); }
        public void SetSeed(long s) { seed = (s ^ 0x5DEECE66DL) & ((1L << 48) - 1); }
        private int Next(int bits) { seed = (seed * 0x5DEECE66DL + 0xBL) & ((1L << 48) - 1); return (int)((ulong)seed >> (48 - bits)); }
        public int NextInt(int bound)
        {
            if ((bound & -bound) == bound) return (int)((bound * (long)Next(31)) >> 31);
            int bits, val;
            do { bits = Next(31); val = bits % bound; } while (bits - val + (bound - 1) < 0);
            return val;
        }
        public float NextFloat() => Next(24) / (float)(1 << 24);
    }

    /// <summary>EnchantmentScreenHandler: bookshelves around the table, three offers, lapis and levels.</summary>
    public class EnchantingScreen : McScreen
    {
        private readonly Vector3Int cell;
        private ItemStack item, lapis;
        private readonly int[] levels = new int[3];
        private readonly string[] clueIds = new string[3];
        private readonly int[] clueLvls = new int[3];
        private static int seed = new System.Random().Next();

        public EnchantingScreen(Vector3Int c)
        {
            cell = c; Background = "gui/container/enchanting_table"; Title = McData.T("container.enchant", "Enchanter");
            Slots.Add(new Slot { X = 15, Y = 47, Get = () => item, Set = s => { item = s; Offers(); }, Max = 1 });
            Slots.Add(new Slot { X = 35, Y = 47, Get = () => lapis, Set = s => lapis = s, CanPlace = s => s.Is("lapis_lazuli") || s.IsSr && s.Actor == Identifiable.Id.Kyanite });
            AddPlayerInventory(8, 84);
        }

        public override IEnumerable<ItemStack> TemporaryStacks() { yield return item; yield return lapis; }
        public override void OnClose() => ReturnToPlayer(new[] { item, lapis });

        /// <summary>EnchantingTableBlock.POWER_PROVIDER_OFFSETS: bookshelves two blocks away with air in between.</summary>
        private int Bookshelves()
        {
            int n = 0;
            for (int x = -2; x <= 2; x++)
                for (int z = -2; z <= 2; z++)
                    for (int y = 0; y <= 1; y++)
                    {
                        if (Mathf.Abs(x) != 2 && Mathf.Abs(z) != 2) continue;
                        var p = cell + new Vector3Int(x, y, z);
                        var mid = cell + new Vector3Int(x / 2, y, z / 2);
                        var b = BlockWorld.Get(p);
                        if (b != null && b.Block.Id == "bookshelf" && !BlockWorld.Has(mid)) n++;
                    }
            return n;
        }

        private void Offers()
        {
            for (int i = 0; i < 3; i++) { levels[i] = 0; clueIds[i] = null; }
            if (item == null || !item.IsMc || item.Item.Enchantability <= 0 || item.Enchanted) return;
            int shelves = Bookshelves();
            var rnd = new JavaRandom(seed);
            for (int i = 0; i < 3; i++)
            {
                levels[i] = Required(rnd, i, shelves, item.Item);
                if (levels[i] < i + 1) levels[i] = 0;
            }
            for (int i = 0; i < 3; i++)
            {
                if (levels[i] <= 0) continue;
                var list = Generate(item, i, levels[i]);
                if (list.Count > 0) { var e = list[new JavaRandom(seed + i).NextInt(list.Count)]; clueIds[i] = e.Key; clueLvls[i] = e.Value; }
            }
        }

        private static int Required(JavaRandom r, int slot, int shelves, ItemDef it)
        {
            if (shelves > 15) shelves = 15;
            int j = r.NextInt(8) + 1 + (shelves >> 1) + r.NextInt(shelves + 1);
            if (slot == 0) return Mathf.Max(j / 3, 1);
            return slot == 1 ? j * 2 / 3 + 1 : Mathf.Max(j, shelves * 2);
        }

        public static List<KeyValuePair<string, int>> Generate(ItemStack stack, int slot, int level, long? forcedSeed = null)
        {
            var r = new JavaRandom(forcedSeed ?? (seed + slot));
            var list = GenerateWith(r, stack, level, false);
            if (stack.Is("book") && list.Count > 1) list.RemoveAt(r.NextInt(list.Count));
            return list;
        }

        /// <summary>EnchantmentHelper.generateEnchantments.</summary>
        public static List<KeyValuePair<string, int>> GenerateWith(JavaRandom r, ItemStack stack, int level, bool treasure)
        {
            var result = new List<KeyValuePair<string, int>>();
            int ench = stack.Item.Enchantability;
            if (ench <= 0) return result;
            level += 1 + r.NextInt(ench / 4 + 1) + r.NextInt(ench / 4 + 1);
            float f = (r.NextFloat() + r.NextFloat() - 1f) * 0.15f;
            level = Mathf.Max(1, Mathf.RoundToInt(level + level * f));
            var pool = Possible(level, stack, treasure);
            if (pool.Count == 0) return result;
            result.Add(Weighted(r, pool));
            while (r.NextInt(50) <= level)
            {
                var last = result[result.Count - 1];
                pool.RemoveAll(p => p.Key == last.Key || McData.Enchants[last.Key].Exclusive.Contains(p.Key));
                if (pool.Count == 0) break;
                result.Add(Weighted(r, pool));
                level /= 2;
            }
            return result;
        }

        private static List<KeyValuePair<string, int>> Possible(int power, ItemStack stack, bool treasure)
        {
            var l = new List<KeyValuePair<string, int>>();
            bool book = stack.Is("book");
            foreach (var e in McData.Enchants.Values)
            {
                if (e.Treasure && !treasure || !e.Random) continue;
                if (!book && !e.Items.Contains(stack.Item.Id)) continue;
                for (int lvl = e.Max; lvl >= 1; lvl--)
                    if (power >= e.MinPower[lvl - 1] && power <= e.MaxPower[lvl - 1]) { l.Add(new KeyValuePair<string, int>(e.Id, lvl)); break; }
            }
            return l;
        }

        private static KeyValuePair<string, int> Weighted(JavaRandom r, List<KeyValuePair<string, int>> pool)
        {
            int total = 0;
            foreach (var p in pool) total += McData.Enchants[p.Key].Weight;
            int i = r.NextInt(Mathf.Max(1, total));
            foreach (var p in pool) { i -= McData.Enchants[p.Key].Weight; if (i < 0) return p; }
            return pool[0];
        }

        protected override void DrawExtra(float mx, float my)
        {
            var bg = McTex.Get(Background);
            int lap = lapis != null ? lapis.Count : 0;
            for (int i = 0; i < 3; i++)
            {
                int x = Left + 60, y = Top + 14 + 19 * i;
                if (levels[i] == 0) { McGui.Blit(bg, x, y, 0, 185, 108, 19); continue; }
                bool can = (GameMode.Creative || lap >= i + 1 && Xp.Level >= levels[i]);
                bool hover = mx >= x && mx < x + 108 && my >= y && my < y + 19;
                McGui.Blit(bg, x, y, 0, can ? (hover ? 204 : 166) : 185, 108, 19);
                McGui.Blit(bg, x + 1, y + 1, 16 * i, can ? 223 : 239, 16, 16);
                // the standard galactic alphabet clue, then the cost in green
                var gal = "§8" + Galactic(i);
                McFont.Draw(gal, x + 20, y + 2, new Color(0.4f, 0.37f, 0.3f), false);
                var cost = levels[i].ToString();
                McFont.Draw(cost, x + 106 - McFont.Width(cost), y + 9, can ? new Color(0.5f, 1f, 0.125f) : new Color(0.25f, 0.5f, 0.06f));
            }
        }

        private string Galactic(int i)
        {
            var words = new[] { "the", "elder", "scrolls", "klaatu", "berata", "niktu", "xyzzy", "bless", "curse", "light", "darkness", "fire", "air", "earth", "water", "hot", "dry", "cold", "wet", "ignite", "snuff", "embiggen", "twist", "shorten", "stretch", "fiddle", "destroy", "imbue", "galvanize", "enchant", "free", "limited", "range", "of", "towards", "inside", "sphere", "cube", "self", "other", "ball", "mental", "physical", "grow", "shrink", "demon", "elemental", "spirit", "animal", "creature", "beast", "humanoid", "undead", "fresh", "stale", "phnglui", "mglwnafh", "cthulhu", "rlyeh", "wgahnagl", "fhtagn", "baguette" };
            var r = new System.Random(seed + i * 7);
            return words[r.Next(words.Length)] + " " + words[r.Next(words.Length)];
        }

        protected override void DrawTooltipsExtra(float mx, float my)
        {
            for (int i = 0; i < 3; i++)
            {
                int x = Left + 60, y = Top + 14 + 19 * i;
                if (levels[i] == 0 || !(mx >= x && mx < x + 108 && my >= y && my < y + 19)) continue;
                var lines = new List<string>();
                if (clueIds[i] != null) lines.Add("§f§o" + McData.Enchants[clueIds[i]].Name + " " + Tooltip.Roman(clueLvls[i]) + " . . . ?");
                lines.Add("");
                lines.Add((lapis != null && lapis.Count >= i + 1 || GameMode.Creative ? "§7" : "§c") + McData.Tf(i == 0 ? "container.enchant.lapis.one" : "container.enchant.lapis.many", i + 1));
                lines.Add((Xp.Level >= levels[i] || GameMode.Creative ? "§7" : "§c") + McData.Tf(i == 0 ? "container.enchant.level.one" : "container.enchant.level.many", i + 1));
                Tooltip.DrawLines(lines, mx, my);
            }
        }

        protected override bool ClickExtra(float mx, float my, int button)
        {
            for (int i = 0; i < 3; i++)
            {
                int x = Left + 60, y = Top + 14 + 19 * i;
                if (!(mx >= x && mx < x + 108 && my >= y && my < y + 19)) continue;
                if (levels[i] == 0 || item == null) return true;
                if (!GameMode.Creative && ((lapis == null || lapis.Count < i + 1) || Xp.Level < levels[i])) return true;
                var list = Generate(item, i, levels[i]);
                if (list.Count == 0) return true;
                if (item.Is("book")) item = new ItemStack(McData.Item("enchanted_book"));
                item.Ench = new Dictionary<string, int>();
                foreach (var e in list) item.Ench[e.Key] = e.Value;
                if (!GameMode.Creative)
                {
                    Xp.AddLevels(-(i + 1));
                    lapis.Count -= i + 1;
                    if (lapis.Count <= 0) lapis = null;
                }
                seed = new System.Random().Next();
                Sounds.Play("block.enchantment_table.use", BlockWorld.Center(cell), 1f, Random.value * 0.1f + 0.9f);
                Offers();
                return true;
            }
            return false;
        }
    }

    /// <summary>AnvilScreenHandler.updateResult: repairing with material or a second item, combining enchantments, renaming, prior work penalty.</summary>
    public class AnvilScreen : McScreen
    {
        private readonly Vector3Int cell;
        private ItemStack a, b, result;
        private int cost, materialUse;
        private string name = "";
        public bool Typing;

        public AnvilScreen(Vector3Int c)
        {
            cell = c; Background = "gui/container/anvil"; Title = McData.T("container.repair", "Réparer et nommer");
            Slots.Add(new Slot { X = 27, Y = 47, Get = () => a, Set = s => { a = s; name = s != null ? s.Name : ""; Recompute(); } });
            Slots.Add(new Slot { X = 76, Y = 47, Get = () => b, Set = s => { b = s; Recompute(); } });
            Slots.Add(new Slot { X = 134, Y = 47, Output = true, Get = () => result, Set = s => result = s, OnTake = Take });
            AddPlayerInventory(8, 84);
        }

        public override IEnumerable<ItemStack> TemporaryStacks() { yield return a; yield return b; }
        public override void OnClose() => ReturnToPlayer(new[] { a, b });

        private static int Penalty(int n) => n * 2 + 1;

        private void Recompute()
        {
            result = null; cost = 0; materialUse = 0;
            if (a == null || !a.IsMc) return;
            var r = a.Copy();
            int i = 0, k = 0;
            int baseCost = a.RepairCost + (b != null ? b.RepairCost : 0);
            var ench = r.Ench != null ? new Dictionary<string, int>(r.Ench) : new Dictionary<string, int>();
            if (b != null && b.IsMc)
            {
                bool book = b.Is("enchanted_book") && b.Enchanted;
                if (r.Damageable && CanRepairWith(a, b))
                {
                    int l = Mathf.Min(r.Damage, r.Item.Durability / 4);
                    if (l <= 0) return;
                    int m;
                    for (m = 0; l > 0 && m < b.Count; m++) { r.Damage -= l; i++; l = Mathf.Min(r.Damage, r.Item.Durability / 4); }
                    materialUse = m;
                }
                else
                {
                    if (!book && (b.Item != r.Item || !r.Damageable)) return;
                    if (r.Damageable && !book)
                    {
                        int left = a.Item.Durability - a.Damage, right = b.Item.Durability - b.Damage;
                        int sum = left + right + r.Item.Durability * 12 / 100;
                        int dmg = Mathf.Max(0, r.Item.Durability - sum);
                        if (dmg < a.Damage) { r.Damage = dmg; i += 2; }
                    }
                    bool any = false, conflict = false;
                    if (b.Ench != null)
                        foreach (var kv in b.Ench)
                        {
                            if (!McData.Enchants.TryGetValue(kv.Key, out var e)) continue;
                            ench.TryGetValue(kv.Key, out int cur);
                            int lvl = cur == kv.Value ? kv.Value + 1 : Mathf.Max(kv.Value, cur);
                            bool ok = r.Is("enchanted_book") || e.Items.Contains(r.Item.Id) || GameMode.Creative;
                            foreach (var other in ench.Keys) if (other != kv.Key && e.Exclusive.Contains(other)) { ok = false; i++; }
                            if (!ok) { conflict = true; continue; }
                            any = true;
                            if (lvl > e.Max) lvl = e.Max;
                            ench[kv.Key] = lvl;
                            int w = e.Weight >= 10 ? 1 : e.Weight >= 5 ? 2 : e.Weight >= 2 ? 4 : 8;
                            if (book) w = Mathf.Max(1, w / 2);
                            i += w * lvl;
                            if (a.Count > 1) i = 40;
                        }
                    if (conflict && !any) return;
                }
            }
            if (!string.IsNullOrEmpty(name) && name != a.Name) { k = 1; i += k; r.CustomName = name; }
            else if (string.IsNullOrEmpty(name) && a.CustomName != null) { k = 1; i += k; r.CustomName = null; }
            cost = baseCost + i;
            if (i <= 0) return;
            if (k == i && k > 0 && cost >= 40) cost = 39;
            if (cost >= 40 && !GameMode.Creative) { result = null; return; }
            int rc = r.RepairCost;
            if (b != null && rc < b.RepairCost) rc = b.RepairCost;
            if (k != i || k == 0) rc = Penalty(rc);
            r.RepairCost = rc;
            r.Ench = ench.Count > 0 ? ench : null;
            result = r;
        }

        private static bool CanRepairWith(ItemStack a, ItemStack b)
        {
            var m = a.Item.ArmorMaterial ?? (a.Item.Id.StartsWith("wooden_") ? "wood" : a.Item.Id.StartsWith("stone_") ? "stone" : a.Item.Id.StartsWith("iron_") ? "iron" : a.Item.Id.StartsWith("golden_") ? "gold" : a.Item.Id.StartsWith("diamond_") ? "diamond" : a.Item.Id.StartsWith("netherite_") ? "netherite" : null);
            switch (m)
            {
                case "wood": return b.HasTag("planks");
                case "stone": return b.HasTag("stone_tool_materials");
                case "iron": case "chainmail": return b.Is("iron_ingot");
                case "gold": return b.Is("gold_ingot");
                case "diamond": return b.Is("diamond");
                case "netherite": return b.Is("netherite_ingot");
                case "leather": return b.Is("leather");
                case "turtle": return b.Is("scute");
            }
            if (a.Is("elytra")) return b.Is("phantom_membrane");
            if (a.Is("shield")) return b.HasTag("planks");
            return false;
        }

        private void Take(ItemStack t)
        {
            if (!GameMode.Creative) Xp.AddLevels(-cost);
            a = null;
            if (materialUse > 0 && b != null) { b.Count -= materialUse; if (b.Count <= 0) b = null; }
            else b = null;
            result = null;
            // AnvilBlock damage: 12% chance to degrade a step
            if (!GameMode.Creative && Random.value < 0.12f)
            {
                var s = BlockWorld.Get(cell);
                string next = s.Block.Id == "anvil" ? "chipped_anvil" : s.Block.Id == "chipped_anvil" ? "damaged_anvil" : null;
                if (next == null) { BlockWorld.Remove(cell); Sounds.Play("block.anvil.destroy", BlockWorld.Center(cell)); Screens.Close(); return; }
                BlockWorld.Set(cell, McData.Block(next).Default.With("facing", s["facing"]));
            }
            Sounds.Play("block.anvil.use", BlockWorld.Center(cell), 1f, Random.value * 0.1f + 0.9f);
        }

        public override bool KeyTyped(char c, KeyCode k) => false;

        protected override void DrawExtra(float mx, float my)
        {
            var bg = McTex.Get(Background);
            McGui.Blit(bg, Left + 59, Top + 20, 0, H + (a != null ? 0 : 16), 110, 16);
            // the name field (clicked to type)
            if (Event.current.type == EventType.KeyDown && Typing && a != null)
            {
                var e = Event.current;
                if (e.keyCode == KeyCode.Backspace && name.Length > 0) { name = name.Substring(0, name.Length - 1); Recompute(); e.Use(); }
                else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.Escape) { Typing = false; e.Use(); }
                else if (e.character >= ' ' && name.Length < 50) { name += e.character; Recompute(); e.Use(); }
            }
            McFont.Draw(name + (Typing && (int)(Time.unscaledTime * 3) % 2 == 0 ? "_" : ""), Left + 62, Top + 24, Color.white);
            if (cost > 0 && a != null)
            {
                bool tooExpensive = cost >= 40 && !GameMode.Creative;
                string txt = tooExpensive ? McData.T("container.repair.expensive", "Trop cher !") : McData.Tf("container.repair.cost", cost);
                bool afford = GameMode.Creative || Xp.Level >= cost;
                var col = tooExpensive || !afford ? new Color(1f, 0.375f, 0.375f) : new Color(0.5f, 1f, 0.125f);
                float tx = Left + W - 8 - McFont.Width(txt) - 2;
                McGui.Fill(tx - 2, Top + 67, McFont.Width(txt) + 4, 12, new Color(0, 0, 0, 0.3f));
                McFont.Draw(txt, tx, Top + 69, col);
            }
        }

        protected override bool ClickExtra(float mx, float my, int button)
        {
            if (mx >= Left + 59 && mx < Left + 169 && my >= Top + 20 && my < Top + 36) { Typing = a != null; return true; }
            if (result != null && my >= Top + 47 && my < Top + 63 && mx >= Left + 134 && mx < Left + 150 && !GameMode.Creative && Xp.Level < cost) return true;
            Typing = false;
            return false;
        }
    }

    /// <summary>BrewingStandScreenHandler; brewing itself runs in Machines.</summary>
    public class BrewingScreen : McScreen
    {
        private readonly BlockEntity e;

        public BrewingScreen(Vector3Int c)
        {
            Background = "gui/container/brewing_stand"; Title = McData.T("container.brewing", "Alambic");
            e = BlockWorld.Entity(c, true, 5);
            if (e.Items == null) e.Items = new ItemStack[5];
            int[][] pos = { new[] { 56, 51 }, new[] { 79, 58 }, new[] { 102, 51 } };
            for (int i = 0; i < 3; i++) { int k = i; Slots.Add(new Slot { X = pos[i][0], Y = pos[i][1], Get = () => e.Items[k], Set = s => e.Items[k] = s, Max = 1, CanPlace = s => s.IsMc && (s.Item.Id.EndsWith("potion") || s.Is("glass_bottle")) }); }
            Slots.Add(new Slot { X = 79, Y = 17, Get = () => e.Items[3], Set = s => e.Items[3] = s, CanPlace = s => Machines.IsBrewIngredient(s) });
            Slots.Add(new Slot { X = 17, Y = 17, Get = () => e.Items[4], Set = s => e.Items[4] = s, CanPlace = s => s.Is("blaze_powder") });
            AddPlayerInventory(8, 84);
        }

        protected override void DrawExtra(float mx, float my)
        {
            var bg = McTex.Get(Background);
            int fuel = Mathf.Clamp((18 * e.Fuel + 20 - 1) / 20, 0, 18);
            if (fuel > 0) McGui.Blit(bg, Left + 60, Top + 44, 176, 29, fuel, 4);
            if (e.Brew > 0)
            {
                int p = (int)(28f * (1f - e.Brew / 400f));
                if (p > 0) McGui.Blit(bg, Left + 97, Top + 16, 176, 0, 9, p);
                int[] bubbles = { 29, 24, 20, 16, 11, 6, 0 };
                int q = bubbles[e.Brew / 2 % 7];
                if (q > 0) McGui.Blit(bg, Left + 63, Top + 14 + 29 - q, 185, 29 - q, 12, q);
            }
        }
    }

    /// <summary>StonecutterScreenHandler: the input and the list of cuts.</summary>
    public class StonecutterScreen : McScreen
    {
        private ItemStack input, output;
        private List<Recipe> recipes = new List<Recipe>();
        private int selected = -1, scroll;

        public StonecutterScreen()
        {
            Background = "gui/container/stonecutter"; Title = McData.T("container.stonecutter", "Tailleur de pierre");
            Slots.Add(new Slot { X = 20, Y = 33, Get = () => input, Set = s => { bool changed = s == null || input == null || !s.SameItem(input); input = s; if (changed) { recipes = Crafting.Stonecutting(input); selected = -1; scroll = 0; } Refresh(); } });
            Slots.Add(new Slot { X = 143, Y = 33, Output = true, Get = () => output, Set = s => output = s, OnTake = t => { if (input != null) { input.Count--; if (input.Count <= 0) { input = null; recipes.Clear(); selected = -1; } } Sounds.Play("ui.stonecutter.take_result", null); Refresh(); } });
            AddPlayerInventory(8, 84);
        }

        public override IEnumerable<ItemStack> TemporaryStacks() { yield return input; }
        public override void OnClose() => ReturnToPlayer(new[] { input });

        private void Refresh() { output = selected >= 0 && selected < recipes.Count && input != null ? ItemStack.Of(recipes[selected].Result, recipes[selected].Count) : null; }

        protected override void DrawExtra(float mx, float my)
        {
            var bg = McTex.Get(Background);
            int rowsTotal = (recipes.Count + 3) / 4;
            McGui.Blit(bg, Left + 119, Top + 15 + (rowsTotal > 3 ? (int)(41f * scroll / Mathf.Max(1, rowsTotal - 3)) : 0), 176 + (rowsTotal > 3 ? 0 : 12), 0, 12, 15);
            for (int i = scroll * 4; i < recipes.Count && i < scroll * 4 + 12; i++)
            {
                int j = i - scroll * 4;
                int x = Left + 52 + j % 4 * 16, y = Top + 15 + j / 4 * 18;
                bool hover = mx >= x && mx < x + 16 && my >= y && my < y + 18;
                McGui.Blit(bg, x, y, 0, 166 + (i == selected ? 18 : hover ? 36 : 0), 16, 18);
                Hud.DrawItem(ItemStack.Of(recipes[i].Result, recipes[i].Count), x, y + 1);
            }
            if (Event.current.type == EventType.ScrollWheel && rowsTotal > 3) { scroll = Mathf.Clamp(scroll + (Event.current.delta.y > 0 ? 1 : -1), 0, rowsTotal - 3); Event.current.Use(); }
        }

        protected override bool ClickExtra(float mx, float my, int button)
        {
            for (int i = scroll * 4; i < recipes.Count && i < scroll * 4 + 12; i++)
            {
                int j = i - scroll * 4;
                int x = Left + 52 + j % 4 * 16, y = Top + 15 + j / 4 * 18;
                if (mx >= x && mx < x + 16 && my >= y && my < y + 18) { selected = i; Sounds.Play("ui.stonecutter.select_recipe", null); Refresh(); return true; }
            }
            return false;
        }
    }

    /// <summary>SmithingScreenHandler (1.20): template + base + addition (netherite upgrades, armour trims).</summary>
    public class SmithingScreen : McScreen
    {
        private ItemStack template, baseItem, addition, output;
        private Recipe recipe;

        public SmithingScreen()
        {
            Background = "gui/container/smithing"; Title = McData.T("container.upgrade", "Améliorer l'équipement");
            Slots.Add(new Slot { X = 8, Y = 48, Get = () => template, Set = s => { template = s; Refresh(); } });
            Slots.Add(new Slot { X = 26, Y = 48, Get = () => baseItem, Set = s => { baseItem = s; Refresh(); } });
            Slots.Add(new Slot { X = 44, Y = 48, Get = () => addition, Set = s => { addition = s; Refresh(); } });
            Slots.Add(new Slot { X = 98, Y = 48, Output = true, Get = () => output, Set = s => output = s, OnTake = t => { Dec(ref template); Dec(ref baseItem); Dec(ref addition); Sounds.Play("block.smithing_table.use", null); Refresh(); } });
            AddPlayerInventory(8, 84);
        }

        private static void Dec(ref ItemStack s) { if (s == null) return; s.Count--; if (s.Count <= 0) s = null; }
        public override IEnumerable<ItemStack> TemporaryStacks() { yield return template; yield return baseItem; yield return addition; }
        public override void OnClose() => ReturnToPlayer(new[] { template, baseItem, addition });
        private void Refresh() { output = template != null && baseItem != null && addition != null ? Crafting.Smithing(template, baseItem, addition, out recipe) : null; }
    }

    /// <summary>GrindstoneScreenHandler: removes enchantments (curses stay), refunds some experience, merges two items.</summary>
    public class GrindstoneScreen : McScreen
    {
        private ItemStack top, bottom, output;

        public GrindstoneScreen()
        {
            Background = "gui/container/grindstone"; Title = McData.T("container.grindstone_title", "Réparer et désenchanter");
            Slots.Add(new Slot { X = 49, Y = 19, Get = () => top, Set = s => { top = s; Refresh(); }, CanPlace = s => s.Damageable || s.Enchanted || s.Is("enchanted_book") });
            Slots.Add(new Slot { X = 49, Y = 40, Get = () => bottom, Set = s => { bottom = s; Refresh(); }, CanPlace = s => s.Damageable || s.Enchanted || s.Is("enchanted_book") });
            Slots.Add(new Slot { X = 129, Y = 34, Output = true, Get = () => output, Set = s => output = s, OnTake = Take });
            AddPlayerInventory(8, 84);
        }

        public override IEnumerable<ItemStack> TemporaryStacks() { yield return top; yield return bottom; }
        public override void OnClose() => ReturnToPlayer(new[] { top, bottom });

        private void Refresh()
        {
            output = null;
            var a = top ?? bottom;
            if (a == null || !a.IsMc) return;
            if (top != null && bottom != null && top.Item != bottom.Item) return;
            var r = a.Copy(1);
            if (top != null && bottom != null && r.Damageable)
            {
                int left = top.Item.Durability - top.Damage + bottom.Item.Durability - bottom.Damage + r.Item.Durability * 5 / 100;
                r.Damage = Mathf.Max(0, r.Item.Durability - left);
            }
            if (r.Ench != null) { var keep = new Dictionary<string, int>(); foreach (var kv in r.Ench) if (McData.Enchants.TryGetValue(kv.Key, out var e) && e.Curse) keep[kv.Key] = kv.Value; r.Ench = keep.Count > 0 ? keep : null; }
            if (r.Is("enchanted_book") && r.Ench == null) r = ItemStack.Of("book");
            r.RepairCost = 0;
            output = r;
        }

        private void Take(ItemStack t)
        {
            // experience refunded: half to all of the enchantments' minimum power
            int xp = 0;
            foreach (var s in new[] { top, bottom })
                if (s?.Ench != null) foreach (var kv in s.Ench) if (McData.Enchants.TryGetValue(kv.Key, out var e) && !e.Curse) xp += e.MinPower[Mathf.Clamp(kv.Value - 1, 0, e.MinPower.Length - 1)];
            if (xp > 0) XpOrb.Spawn(Player.main.transform.position, Mathf.CeilToInt(xp / 2f) + Random.Range(0, Mathf.CeilToInt(xp / 2f) + 1));
            top = null; bottom = null;
            Sounds.Play("block.grindstone.use", null);
            Refresh();
        }
    }
}
