using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// The creative inventory (CreativeInventoryScreen): Minecraft's tabs filled with every item of the game,
    /// a Subnautica tab with every Subnautica item, the search tab and the survival inventory tab with the
    /// destroy slot. Left click takes one, middle click a full stack, shift-click puts a full stack in the hotbar.
    /// </summary>
    public class CreativeScreen : McScreen
    {
        private class Tab { public string Key, Name; public ItemStack Icon; public List<ItemStack> Items; public bool Top; public int Column; }
        private static List<Tab> tabs;
        private int tab;
        private float scroll;
        private string search = "";
        public bool Typing => tabs != null && tabs[tab].Key == "search";
        private readonly ItemStack[] view = new ItemStack[45];
        private List<ItemStack> shown = new List<ItemStack>();
        private static int lastTab;
        private bool draggingScroll;

        public override bool ShowPlayerInvTitle => false;

        public CreativeScreen()
        {
            W = 195; H = 136;
            BuildTabs();
            tab = lastTab;
            Select(tab);
        }

        public static bool InCreativeArea(McScreen s, float mx, float my) => s is CreativeScreen && my >= s.Top - 32 && my < s.Top + s.H + 32 && mx >= s.Left && mx < s.Left + s.W;

        private static void BuildTabs()
        {
            if (tabs != null) return;
            tabs = new List<Tab>();
            Tab T(string key, string name, string icon, bool top, int col) { var t = new Tab { Key = key, Name = name, Icon = ItemStack.Of(icon), Items = new List<ItemStack>(), Top = top, Column = col }; tabs.Add(t); return t; }
            var building = T("building", McData.T("itemGroup.buildingBlocks"), "bricks", true, 0);
            var colored = T("colored", McData.T("itemGroup.coloredBlocks"), "cyan_wool", true, 1);
            var natural = T("natural", McData.T("itemGroup.natural"), "grass_block", true, 2);
            var functional = T("functional", McData.T("itemGroup.functional"), "crafting_table", true, 3);
            var redstone = T("redstone", McData.T("itemGroup.redstone"), "redstone", true, 4);
            var search = T("search", McData.T("itemGroup.search"), "compass", true, 6);
            var tools = T("tools", McData.T("itemGroup.tools"), "diamond_pickaxe", false, 0);
            var combat = T("combat", McData.T("itemGroup.combat"), "netherite_sword", false, 1);
            var food = T("food", McData.T("itemGroup.foodAndDrink"), "golden_apple", false, 2);
            var ingredients = T("ingredients", McData.T("itemGroup.ingredients"), "iron_ingot", false, 3);
            var eggs = T("eggs", McData.T("itemGroup.spawnEggs"), "cod_spawn_egg", false, 4);
            var sn = new Tab { Key = "subnautica", Name = "Subnautica", Icon = new ItemStack(TechType.Seaglide), Items = new List<ItemStack>(), Top = false, Column = 5 };
            tabs.Add(sn);
            var inv = T("inventory", McData.T("itemGroup.inventory"), "chest", false, 6);

            string[] colors = { "white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray", "light_gray", "cyan", "purple", "blue", "brown", "green", "red", "black" };
            foreach (var d in McData.ItemList)
            {
                var id = d.Id;
                if (id.EndsWith("_spawn_egg")) { if (Mobs.IsMcMob(id.Replace("_spawn_egg", ""))) eggs.Items.Add(new ItemStack(d)); continue; }
                if (id == "air" || id.Contains("command_block") || id == "barrier" || id == "structure_void" || id == "debug_stick" || id == "jigsaw" || id == "structure_block" || id == "light" || id == "knowledge_book") continue;
                var s = new ItemStack(d);
                if (id == "potion" || id == "splash_potion" || id == "lingering_potion" || id == "tipped_arrow")
                {
                    foreach (var p in McData.Potions.Keys) { var ps = new ItemStack(d) { Potion = p }; (id == "tipped_arrow" ? combat : food).Items.Add(ps); }
                    continue;
                }
                if (id == "enchanted_book")
                {
                    foreach (var e in McData.Enchants.Values) for (int l = 1; l <= e.Max; l++) ingredients.Items.Add(new ItemStack(d) { Ench = new Dictionary<string, int> { { e.Id, l } } });
                    continue;
                }
                if (id == "firework_rocket") { for (int f = 1; f <= 3; f++) tools.Items.Add(new ItemStack(d) { Flight = f }); continue; }
                bool isColored = false;
                foreach (var c in colors) if (id.StartsWith(c + "_") && d.Block != null) { isColored = true; break; }
                if (d.Food != null) food.Items.Add(s);
                else if (d.Class == "SwordItem" || d.Class == "BowItem" || d.Class == "CrossbowItem" || d.Class == "TridentItem" || d.Class == "ShieldItem" || d.ArmorSlot != null || id == "arrow" || id == "spectral_arrow" || id == "totem_of_undying" || d.Class == "AxeItem" && false) combat.Items.Add(s);
                else if (d.IsTool || d.Class == "FishingRodItem" || d.Class == "FlintAndSteelItem" || d.Class == "ShearsItem" || d.Class == "BucketItem" || id.EndsWith("_bucket") || id == "compass" || id == "clock" || id == "spyglass" || id == "lead" || id == "name_tag" || id == "bone_meal" || id == "ender_pearl" || id == "ender_eye" || id == "elytra" && false || id.StartsWith("music_disc") || id.EndsWith("_boat") || id.EndsWith("minecart") || id == "saddle" || id == "map") tools.Items.Add(s);
                else if (d.Block != null)
                {
                    var b = d.Block;
                    if (isColored) colored.Items.Add(s);
                    else if (b.Class.Contains("Redstone") || b.Class.Contains("Piston") || b.Class.Contains("Rail") || b.Class.Contains("PressurePlate") || b.Class.Contains("Button") || b.Class.Contains("Lever") || b.Class.Contains("Repeater") || b.Class.Contains("Comparator") || b.Class.Contains("Observer") || b.Class.Contains("Hopper") || b.Class.Contains("Dispenser") || b.Class.Contains("Dropper") || b.Class.Contains("Tnt") || b.Class.Contains("Lamp") || b.Class.Contains("Target") || b.Class.Contains("Daylight") || b.Class.Contains("Tripwire") || b.Class.Contains("NoteBlock") || b.Class == "TrapdoorBlock" || b.Class == "DoorBlock" && id.Contains("iron")) redstone.Items.Add(s);
                    else if (b.Class.Contains("Chest") || b.Class.Contains("Furnace") || b.Class.Contains("Smoker") || b.Class.Contains("Crafting") || b.Class.Contains("Table") || b.Class.Contains("Anvil") || b.Class.Contains("Barrel") || b.Class.Contains("Brewing") || b.Class.Contains("Cauldron") || b.Class.Contains("Stonecutter") || b.Class.Contains("Grindstone") || b.Class.Contains("Loom") || b.Class.Contains("Bell") || b.Class.Contains("Lantern") || b.Class.Contains("Torch") || b.Class.Contains("Campfire") || b.Class.Contains("Sign") || b.Class.Contains("Bed") || b.Class.Contains("Ladder") || b.Class.Contains("Scaffolding") || b.Class.Contains("Beacon") || b.Class.Contains("Conduit") || b.Class.Contains("FlowerPot") || b.Class.Contains("Skull") || b.Class.Contains("Banner") || b.Class.Contains("Lectern") || b.Class.Contains("Composter") || b.Class.Contains("Jukebox") || b.Class.Contains("Candle") || b.Class.Contains("Chain") || b.Class.Contains("EndRod") || b.Class.Contains("Respawn")) functional.Items.Add(s);
                    else if (id.Contains("_ore") || id.EndsWith("_log") || id.EndsWith("_leaves") || id.EndsWith("_sapling") || b.Class.Contains("Flower") || b.Class.Contains("Plant") || b.Class.Contains("Coral") || b.Class.Contains("Mushroom") || b.Class.Contains("Grass") || b.Class.Contains("Fern") || b.Class.Contains("Kelp") || b.Class.Contains("Seagrass") || b.Class.Contains("Sand") || b.Class.Contains("Gravel") || b.Class.Contains("Snow") || b.Class.Contains("Ice") || b.Class.Contains("Cactus") || b.Class.Contains("Vine") || b.Class.Contains("Bush") || b.Class.Contains("Azalea") || b.Class.Contains("Amethyst") || b.Class.Contains("Dripstone") || id == "dirt" || id == "coarse_dirt" || id == "podzol" || id == "mycelium" || id == "clay" || id == "mud" || id == "moss_block" || id == "stone" || id == "deepslate" || id == "tuff" || id == "calcite" || id == "obsidian" || id == "netherrack" || id == "end_stone" || id == "bedrock" || id == "pumpkin" || id == "melon") natural.Items.Add(s);
                    else building.Items.Add(s);
                }
                else if (d.Class.Contains("Spawn")) eggs.Items.Add(s);
                else ingredients.Items.Add(s);
            }
            // Subnautica: every item that has an inventory icon
            var seen = new HashSet<string>();
            foreach (TechType t in System.Enum.GetValues(typeof(TechType)))
            {
                if (t == TechType.None) continue;
                var name = t.ToString();
                if (!seen.Add(name)) continue;
                Sprite sp = null;
                try { sp = SpriteManager.Get(SpriteManager.Group.Item, t.AsString(), null); } catch { }
                if (sp == null) continue;
                if (name.EndsWith("Fragment") || name.EndsWith("Blueprint") || name.Contains("Precursor") && name.Contains("Key") && false) continue;
                sn.Items.Add(new ItemStack(t, 1));
            }
            search.Items = new List<ItemStack>();
            foreach (var t in tabs) if (t != search && t != inv) search.Items.AddRange(t.Items);
        }

        private void Select(int i)
        {
            tab = Mathf.Clamp(i, 0, tabs.Count - 1);
            lastTab = tab;
            scroll = 0;
            Slots.Clear();
            var t = tabs[tab];
            if (t.Key == "inventory")
            {
                // CreativeInventoryScreen inventory tab layout
                for (int k = 0; k < 4; k++)
                {
                    int ai = 3 - k;
                    string slot = ai == 3 ? "head" : ai == 2 ? "chest" : ai == 1 ? "legs" : "feet";
                    Slots.Add(new Slot { X = 54 + (k / 2) * 54, Y = 6 + (k % 2) * 27, Get = () => Inventory.Armor[ai], Set = s => Inventory.Armor[ai] = s, Max = 1, CanPlace = s => s.IsMc && (s.Item.ArmorSlot == slot || slot == "chest" && s.Is("elytra")) });
                }
                Slots.Add(new Slot { X = 35, Y = 20, Get = () => Inventory.Offhand, Set = s => Inventory.Offhand = s });
                for (int r = 0; r < 3; r++) for (int c = 0; c < 9; c++) { int idx = 9 + r * 9 + c; Slots.Add(new Slot { X = 9 + c * 18, Y = 54 + r * 18, Get = () => Inventory.Main[idx], Set = s => Inventory.Main[idx] = s, PlayerInv = true }); }
                Slots.Add(new Slot { X = 173, Y = 112, Get = () => null, Set = s => { }, CanPlace = s => true }); // destroy item slot
            }
            else
            {
                for (int k = 0; k < 45; k++)
                {
                    int kk = k;
                    Slots.Add(new Slot { X = 9 + k % 9 * 18, Y = 18 + k / 9 * 18, Get = () => view[kk], Set = s => { }, CanPlace = s => true });
                }
            }
            for (int c = 0; c < 9; c++) { int idx = c; Slots.Add(new Slot { X = 9 + c * 18, Y = 112, Get = () => Inventory.Main[idx], Set = s => Inventory.Main[idx] = s, PlayerInv = true, Hotbar = true }); }
            Filter();
        }

        private void Filter()
        {
            var t = tabs[tab];
            if (t.Key == "search")
            {
                shown = new List<ItemStack>();
                var q = search.ToLowerInvariant();
                foreach (var s in t.Items) if (q.Length == 0 || s.Name.ToLowerInvariant().Contains(q) || s.Id.Contains(q)) shown.Add(s);
            }
            else shown = t.Items;
            Refresh();
        }

        private void Refresh()
        {
            int rows = Mathf.Max(0, (shown.Count + 8) / 9 - 5);
            int start = Mathf.RoundToInt(scroll * rows) * 9;
            for (int k = 0; k < 45; k++) view[k] = start + k < shown.Count ? shown[start + k] : null;
        }

        private bool IsCreativeSlot(Slot s) => tabs[tab].Key != "inventory" && Slots.IndexOf(s) < 45;
        private bool IsDestroy(Slot s) => tabs[tab].Key == "inventory" && Slots.IndexOf(s) == 32;

        protected override void Click(Slot slot, int button)
        {
            if (IsDestroy(slot)) { Inventory.Cursor = null; return; }
            if (!IsCreativeSlot(slot)) { base.Click(slot, button); return; }
            var st = slot.Stack;
            if (Inventory.Cursor != null) { Inventory.Cursor = null; return; } // dropping on the item list deletes
            if (st == null) return;
            var give = st.Copy(button == 2 ? st.MaxStack : 1);
            if (give.IsSn && !SnBridge.IsVirtual(give.Tech)) { SnBridge.GiveReal(give.Tech, 1); return; }
            Inventory.Cursor = give;
        }

        protected override void QuickMove(Slot from)
        {
            if (IsCreativeSlot(from))
            {
                var st = from.Stack;
                if (st == null) return;
                if (st.IsSn && !SnBridge.IsVirtual(st.Tech)) { SnBridge.GiveReal(st.Tech, 1); return; }
                Inventory.Add(st.Copy(st.MaxStack));
                return;
            }
            if (tabs[tab].Key != "inventory") { from.Stack = null; return; } // shift-click on the hotbar in an item tab clears it
            base.QuickMove(from);
        }

        public override void Tick()
        {
            float wheel = Input.mouseScrollDelta.y;
            int rows = Mathf.Max(0, (shown.Count + 8) / 9 - 5);
            if (wheel != 0 && rows > 0 && tabs[tab].Key != "inventory") { scroll = Mathf.Clamp01(scroll - wheel / rows); Refresh(); }
            if (draggingScroll)
            {
                if (!Input.GetMouseButton(0)) draggingScroll = false;
                else
                {
                    float my = (Screen.height - Input.mousePosition.y) / McGui.Scale;
                    scroll = Mathf.Clamp01((my - Top - 18 - 7.5f) / (112 - 18 - 15f));
                    Refresh();
                }
            }
        }

        protected override bool ClickExtra(float mx, float my, int button)
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                var t = tabs[i];
                float x = Left + t.Column * 27 + (t.Column == 6 ? W - 27 * 7 - 1 + 0 : 0);
                if (t.Column == 6) x = Left + W - 28;
                float y = t.Top ? Top - 28 : Top + H - 4;
                if (mx >= x && mx < x + 26 && my >= y && my < y + 32) { Select(i); Sounds.Play("ui.button.click", null, 0.25f); return true; }
            }
            if (tabs[tab].Key != "inventory" && mx >= Left + 175 && mx < Left + 189 && my >= Top + 18 && my < Top + 112) { draggingScroll = true; return true; }
            return false;
        }

        protected override void DrawBackground(float mx, float my)
        {
            var tabsTex = McTex.Get("gui/container/creative_inventory/tabs");
            // unselected tabs behind the window
            for (int i = 0; i < tabs.Count; i++) if (i != tab) DrawTab(i, tabsTex, false);
            var t = tabs[tab];
            string bg = t.Key == "inventory" ? "gui/container/creative_inventory/tab_inventory" : t.Key == "search" ? "gui/container/creative_inventory/tab_item_search" : "gui/container/creative_inventory/tab_items";
            McGui.Blit(McTex.Get(bg), Left, Top, 0, 0, W, H);
            if (t.Key != "inventory")
            {
                int rows = Mathf.Max(0, (shown.Count + 8) / 9 - 5);
                McGui.Blit(tabsTex, Left + 175, Top + 18 + (int)((112 - 18 - 17) * scroll), 232 + (rows > 0 ? 0 : 12), 0, 12, 15);
            }
            DrawTab(tab, tabsTex, true);
            McFont.Draw(t.Name, Left + 8, Top + 6, new Color(0.25f, 0.25f, 0.25f), false);
            if (t.Key == "search")
            {
                McFont.Draw(search + ((int)(Time.unscaledTime * 3) % 2 == 0 ? "_" : ""), Left + 82, Top + 6, Color.white);
                var e = Event.current;
                if (e.type == EventType.KeyDown)
                {
                    if (e.keyCode == KeyCode.Backspace && search.Length > 0) { search = search.Substring(0, search.Length - 1); Filter(); e.Use(); }
                    else if (e.character >= ' ') { search += e.character; Filter(); e.Use(); }
                }
            }
            if (t.Key == "inventory") InventoryPreview.Draw(Left + 73, Top + 6, 32, 43, mx, my);
        }

        private void DrawTab(int i, Texture2D tex, bool selected)
        {
            var t = tabs[i];
            int col = t.Column;
            float x = Left + col * 27;
            if (col == 6) x = Left + W - 28;
            float y = t.Top ? Top - 28 : Top + H - 4;
            int u = col * 26, v = (t.Top ? 0 : 64) + (selected ? 32 : 0);
            if (col == 6) u = 6 * 26 - 2;
            McGui.Blit(tex, x, y, u, v, 26, 32);
            Hud.DrawItem(t.Icon, x + 5, y + (t.Top ? 9 : 7));
        }

        protected override void DrawTooltipsExtra(float mx, float my)
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                var t = tabs[i];
                float x = Left + t.Column * 27; if (t.Column == 6) x = Left + W - 28;
                float y = t.Top ? Top - 28 : Top + H - 4;
                if (mx >= x && mx < x + 26 && my >= y && my < y + 32) Tooltip.DrawLines(new List<string> { t.Name }, mx, my);
            }
        }
    }

    /// <summary>Block entities that keep working with no screen open: furnaces (AbstractFurnaceBlockEntity.tick) and brewing stands.</summary>
    public static class Machines
    {
        public static bool IsBrewIngredient(ItemStack s)
        {
            if (s == null || !s.IsMc) return false;
            if (s.Is("gunpowder") || s.Is("dragon_breath")) return true;
            foreach (var r in McData.Brewing) foreach (var i in (Newtonsoft.Json.Linq.JArray)r["ingredient"]) if ((string)i == s.Item.Id) return true;
            return false;
        }

        public static void Tick()
        {
            foreach (var kv in BlockWorld.Entities)
            {
                var s = BlockWorld.Get(kv.Key);
                if (s == null) continue;
                switch (s.Block.Class)
                {
                    case "FurnaceBlock": Furnace(kv.Key, s, kv.Value, "smelting", 1); break;
                    case "BlastFurnaceBlock": Furnace(kv.Key, s, kv.Value, "blasting", 2); break;
                    case "SmokerBlock": Furnace(kv.Key, s, kv.Value, "smoking", 2); break;
                    case "BrewingStandBlock": Brew(kv.Key, kv.Value); break;
                }
            }
        }

        private static readonly List<KeyValuePair<Vector3Int, BState>> pending = new List<KeyValuePair<Vector3Int, BState>>();

        public static void Flush()
        {
            foreach (var p in pending) BlockWorld.Set(p.Key, p.Value, false);
            pending.Clear();
        }

        private static void Furnace(Vector3Int c, BState s, BlockEntity e, string type, int speed)
        {
            if (e.Items == null) e.Items = new ItemStack[3];
            bool wasBurning = e.Burn > 0;
            if (e.Burn > 0) e.Burn--;
            var input = e.Items[0];
            var fuel = e.Items[1];
            var result = Crafting.SmeltRecipe(type, input, out var recipe);
            bool canOutput = result != null && (e.Items[2] == null || e.Items[2].SameItem(result) && e.Items[2].Count + result.Count <= e.Items[2].MaxStack);
            if (e.Burn <= 0 && canOutput && fuel != null)
            {
                int ft = Crafting.FuelTime(fuel);
                if (ft > 0)
                {
                    e.Burn = e.BurnMax = ft / speed; // blast furnaces and smokers burn fuel twice as fast
                    if (fuel.Is("lava_bucket")) e.Items[1] = ItemStack.Of("bucket");
                    else { fuel.Count--; if (fuel.Count <= 0) e.Items[1] = null; }
                }
            }
            if (e.Burn > 0 && canOutput)
            {
                e.CookMax = recipe != null && recipe.Time > 0 ? recipe.Time : 200 / speed;
                if (++e.Cook >= e.CookMax)
                {
                    e.Cook = 0;
                    if (e.Items[2] == null) e.Items[2] = result; else e.Items[2].Count += result.Count;
                    input.Count--; if (input.Count <= 0) e.Items[0] = null;
                    e.Xp += recipe != null ? recipe.Xp : 0.1f;
                }
            }
            else if (e.Cook > 0) e.Cook = Mathf.Max(0, e.Cook - 2);
            bool burning = e.Burn > 0;
            if (burning != wasBurning && s.Block.HasProp("lit")) pending.Add(new KeyValuePair<Vector3Int, BState>(c, s.With("lit", burning ? "true" : "false")));
            if (burning && Random.Range(0, 100) == 0) Sounds.Play(type == "blasting" ? "block.blastfurnace.fire_crackle" : type == "smoking" ? "block.smoker.smoke" : "block.furnace.fire_crackle", BlockWorld.Center(c));
        }

        private static void Brew(Vector3Int c, BlockEntity e)
        {
            if (e.Items == null) e.Items = new ItemStack[5];
            if (e.Fuel <= 0 && e.Items[4] != null && e.Items[4].Is("blaze_powder")) { e.Fuel = 20; e.Items[4].Count--; if (e.Items[4].Count <= 0) e.Items[4] = null; }
            var ing = e.Items[3];
            bool can = ing != null && Brewable(e);
            if (e.Brew > 0)
            {
                if (!can) { e.Brew = 0; return; }
                if (--e.Brew == 0)
                {
                    for (int i = 0; i < 3; i++) e.Items[i] = BrewResult(e.Items[i], ing) ?? e.Items[i];
                    ing.Count--; if (ing.Count <= 0) e.Items[3] = ing.Is("dragon_breath") ? null : null;
                    if (ing.Count <= 0) e.Items[3] = null;
                    Sounds.Play("block.brewing_stand.brew", BlockWorld.Center(c));
                }
            }
            else if (can && e.Fuel > 0) { e.Fuel--; e.Brew = 400; }
        }

        private static bool Brewable(BlockEntity e)
        {
            for (int i = 0; i < 3; i++) if (BrewResult(e.Items[i], e.Items[3]) != null) return true;
            return false;
        }

        /// <summary>BrewingRecipeRegistry: potion recipes from the dump, plus the item conversions (splash, lingering).</summary>
        private static ItemStack BrewResult(ItemStack potion, ItemStack ing)
        {
            if (potion == null || ing == null || !potion.IsMc) return null;
            if (ing.Is("gunpowder") && potion.Is("potion")) return new ItemStack(McData.Item("splash_potion")) { Potion = potion.Potion };
            if (ing.Is("dragon_breath") && potion.Is("splash_potion")) return new ItemStack(McData.Item("lingering_potion")) { Potion = potion.Potion };
            if (!potion.Item.Id.EndsWith("potion")) return null;
            foreach (var r in McData.Brewing)
            {
                if ((string)r["input"] != (potion.Potion ?? "water")) continue;
                bool ok = false;
                foreach (var i in (Newtonsoft.Json.Linq.JArray)r["ingredient"]) if ((string)i == ing.Item.Id) ok = true;
                if (ok) return new ItemStack(potion.Item) { Potion = (string)r["output"] };
            }
            return null;
        }
    }
}
