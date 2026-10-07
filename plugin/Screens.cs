using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>A slot of a container screen (Minecraft's Slot): where it is and how it reads / writes its stack.</summary>
    public class Slot
    {
        public int X, Y;
        public Func<ItemStack> Get;
        public Action<ItemStack> Set;
        public Func<ItemStack, bool> CanPlace = s => true;
        public bool Output;                 // result slots: take only, whole stack
        public Action<ItemStack> OnTake;    // after taking from an output slot
        public int Max = 64;
        public bool PlayerInv;              // part of the player's inventory (for shift-click routing)
        public bool Hotbar;
        public int Group;                   // shift-click target groups

        public ItemStack Stack { get => Get(); set => Set(value); }
    }

    /// <summary>
    /// HandledScreen / ScreenHandler: the background, the slots and Minecraft's exact mouse behaviour (left click
    /// picks up / places / merges / swaps, right click halves / places one, shift-click moves between inventories,
    /// double click collects, dragging spreads, 1-9 swaps with the hotbar, Q throws, middle click clones in creative)
    /// with tooltips. Concrete screens add their slots, background and extra drawing.
    /// </summary>
    public abstract class McScreen
    {
        public int W = 176, H = 166;
        public string Title;
        public readonly List<Slot> Slots = new List<Slot>();
        public int Left, Top;
        protected string Background;
        protected int BgW = 256, BgH = 256;
        public virtual bool HideHand => true;
        public virtual bool ShowPlayerInvTitle => true;

        protected Slot hovered;
        private readonly List<Slot> drag = new List<Slot>();
        private int dragButton = -1;
        private float lastClick; private Slot lastClickSlot; private ItemStack lastClickStack;

        protected void AddPlayerInventory(int x, int y)
        {
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 9; c++)
                {
                    int idx = 9 + r * 9 + c;
                    Slots.Add(new Slot { X = x + c * 18, Y = y + r * 18, Get = () => Inventory.Main[idx], Set = s => Inventory.Main[idx] = s, PlayerInv = true, Group = 1 });
                }
            for (int c = 0; c < 9; c++)
            {
                int idx = c;
                Slots.Add(new Slot { X = x + c * 18, Y = y + 58, Get = () => Inventory.Main[idx], Set = s => Inventory.Main[idx] = s, PlayerInv = true, Hotbar = true, Group = 2 });
            }
        }

        public virtual void OnOpen() { }
        public virtual void OnClose() { }
        public virtual void Tick() { }
        protected virtual void DrawExtra(float mx, float my) { }
        protected virtual bool ClickExtra(float mx, float my, int button) => false;
        public virtual bool KeyTyped(char c, KeyCode k) => false;

        /// <summary>Items of Subnautica sitting in this screen's temporary grids (they still belong to the player).</summary>
        public virtual IEnumerable<ItemStack> TemporaryStacks() { yield break; }

        // ------------------------------------------------------------------------------------------------ input

        public void Update()
        {
            int w = McGui.ScaledWidth, h = McGui.ScaledHeight;
            Left = (w - W) / 2; Top = (h - H) / 2;
            float mx = Input.mousePosition.x / McGui.Scale, my = (Screen.height - Input.mousePosition.y) / McGui.Scale;
            hovered = SlotAt(mx, my);
            Tick();

            for (int b = 0; b < 3; b++)
            {
                if (Input.GetMouseButtonDown(b)) MouseDown(mx, my, b);
                if (Input.GetMouseButton(b) && dragButton == b && Inventory.Cursor != null) DragOver(hovered);
                if (Input.GetMouseButtonUp(b)) MouseUp(b);
            }
            if (hovered != null)
            {
                for (int i = 0; i < 9; i++) if (Input.GetKeyDown(KeyCode.Alpha1 + i)) SwapHotbar(hovered, i);
                if (Input.GetKeyDown(KeyCode.F)) SwapOffhand(hovered);
                if (Input.GetKeyDown(Game.DropKey) && hovered.Stack != null && Inventory.Cursor == null)
                {
                    var s = hovered.Stack;
                    int n = Input.GetKey(KeyCode.LeftControl) ? s.Count : 1;
                    var drop = s.Copy(n);
                    if (hovered.Output) { drop = s.Copy(); hovered.OnTake?.Invoke(drop); }
                    else { s.Count -= n; hovered.Stack = s.Count > 0 ? s : null; }
                    Interaction.Throw(drop);
                }
            }
        }

        protected Slot SlotAt(float mx, float my)
        {
            foreach (var s in Slots)
                if (mx >= Left + s.X - 1 && mx < Left + s.X + 17 && my >= Top + s.Y - 1 && my < Top + s.Y + 17) return s;
            return null;
        }

        private void MouseDown(float mx, float my, int button)
        {
            if (ClickExtra(mx, my, button)) return;
            var slot = SlotAt(mx, my);
            bool outside = mx < Left || my < Top || mx >= Left + W || my >= Top + H;
            if (slot == null)
            {
                if (outside && Inventory.Cursor != null && !CreativeScreen.InCreativeArea(this, mx, my))
                {
                    // clicking outside the window throws the carried stack (right: one item)
                    if (button == 0) { Interaction.Throw(Inventory.Cursor); Inventory.Cursor = null; }
                    else if (button == 1) { var one = Inventory.Cursor.Copy(1); Inventory.Cursor.Count--; if (Inventory.Cursor.Count <= 0) Inventory.Cursor = null; Interaction.Throw(one); }
                }
                return;
            }
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            // double click: collect matching items into the cursor (PICKUP_ALL)
            if (button == 0 && !shift && Time.unscaledTime - lastClick < 0.25f && lastClickSlot == slot && Inventory.Cursor != null && !slot.Output)
            {
                CollectAll(Inventory.Cursor);
                lastClick = 0;
                return;
            }
            lastClick = Time.unscaledTime; lastClickSlot = slot;
            if (button == 2) { if (GameMode.Creative && slot.Stack != null && Inventory.Cursor == null) { Inventory.Cursor = slot.Stack.Copy(slot.Stack.MaxStack); } return; }
            if (shift && !slot.Output) { QuickMove(slot); return; }
            if (shift && slot.Output) { QuickMoveOutput(slot); return; }
            if (Inventory.Cursor != null && !slot.Output && (slot.Stack == null || slot.Stack.SameItem(Inventory.Cursor)) && slot.CanPlace(Inventory.Cursor))
            {
                // start a drag; a single click is handled as a drag over one slot
                dragButton = button;
                drag.Clear();
                drag.Add(slot);
                return;
            }
            Click(slot, button);
        }

        private void DragOver(Slot s)
        {
            if (s == null || drag.Contains(s) || s.Output) return;
            var c = Inventory.Cursor;
            if (c == null || (s.Stack != null && !s.Stack.SameItem(c)) || !s.CanPlace(c)) return;
            if (drag.Count >= c.Count && dragButton == 0) return;
            drag.Add(s);
        }

        private void MouseUp(int button)
        {
            if (dragButton != button) return;
            dragButton = -1;
            var c = Inventory.Cursor;
            if (c == null || drag.Count == 0) { drag.Clear(); return; }
            if (drag.Count == 1) { Click(drag[0], button); drag.Clear(); return; }
            // QUICK_CRAFT: left spreads evenly, right places one in each
            int per = button == 0 ? Mathf.Max(1, c.Count / drag.Count) : 1;
            foreach (var s in drag)
            {
                if (c == null || c.Count <= 0) break;
                int room = Mathf.Min(s.Max, c.MaxStack) - (s.Stack?.Count ?? 0);
                int n = Mathf.Min(per, Mathf.Min(room, c.Count));
                if (n <= 0) continue;
                if (s.Stack == null) s.Stack = c.Copy(n); else { var st = s.Stack; st.Count += n; s.Stack = st; }
                c.Count -= n;
            }
            if (c != null && c.Count <= 0) Inventory.Cursor = null;
            drag.Clear();
            Changed();
        }

        /// <summary>SlotActionType.PICKUP: Minecraft's click on a slot.</summary>
        protected virtual void Click(Slot slot, int button)
        {
            var cur = Inventory.Cursor;
            var st = slot.Stack;
            if (slot.Output)
            {
                if (st == null) return;
                if (cur == null) { Inventory.Cursor = st.Copy(); slot.OnTake?.Invoke(st); }
                else if (cur.SameItem(st) && cur.Count + st.Count <= cur.MaxStack) { cur.Count += st.Count; slot.OnTake?.Invoke(st); }
                Changed();
                return;
            }
            if (cur == null)
            {
                if (st == null) return;
                if (button == 0) { Inventory.Cursor = st; slot.Stack = null; }
                else
                {
                    int take = (st.Count + 1) / 2;
                    Inventory.Cursor = st.Copy(take);
                    st.Count -= take;
                    slot.Stack = st.Count > 0 ? st : null;
                }
            }
            else if (st == null)
            {
                if (!slot.CanPlace(cur)) return;
                int n = button == 0 ? Mathf.Min(cur.Count, Mathf.Min(slot.Max, cur.MaxStack)) : 1;
                slot.Stack = cur.Copy(n);
                cur.Count -= n;
                if (cur.Count <= 0) Inventory.Cursor = null;
            }
            else if (st.SameItem(cur))
            {
                int max = Mathf.Min(slot.Max, st.MaxStack);
                int n = button == 0 ? Mathf.Min(cur.Count, max - st.Count) : Mathf.Min(1, max - st.Count);
                if (n > 0) { st.Count += n; cur.Count -= n; slot.Stack = st; if (cur.Count <= 0) Inventory.Cursor = null; }
            }
            else if (slot.CanPlace(cur) && cur.Count <= slot.Max)
            {
                slot.Stack = cur; Inventory.Cursor = st;
            }
            Changed();
        }

        protected virtual void Changed() { }

        private void CollectAll(ItemStack cur)
        {
            for (int pass = 0; pass < 2; pass++)
                foreach (var s in Slots)
                {
                    if (cur.Count >= cur.MaxStack) return;
                    var st = s.Stack;
                    if (st == null || s.Output || !st.SameItem(cur)) continue;
                    if (pass == 0 && st.Count == st.MaxStack) continue;
                    int n = Mathf.Min(st.Count, cur.MaxStack - cur.Count);
                    cur.Count += n; st.Count -= n;
                    s.Stack = st.Count > 0 ? st : null;
                }
            Changed();
        }

        /// <summary>transferSlot: shift-click moves between this container and the player's inventory.</summary>
        protected virtual void QuickMove(Slot from)
        {
            var st = from.Stack;
            if (st == null) return;
            IEnumerable<Slot> targets;
            if (from.PlayerInv)
            {
                var container = Slots.FindAll(s => !s.PlayerInv && !s.Output && s.CanPlace(st));
                if (container.Count > 0) targets = container;
                else targets = Slots.FindAll(s => s.PlayerInv && s.Hotbar != from.Hotbar); // hotbar <-> main
            }
            else
            {
                var hot = Slots.FindAll(s => s.Hotbar); hot.Reverse();
                var main = Slots.FindAll(s => s.PlayerInv && !s.Hotbar); main.Reverse();
                var l = new List<Slot>(); l.AddRange(main); l.AddRange(hot);
                l.Reverse();
                targets = l;
            }
            Insert(st, targets);
            from.Stack = st.Count > 0 ? st : null;
            Changed();
        }

        private void QuickMoveOutput(Slot from)
        {
            // crafting results: craft as many as fit
            for (int guard = 0; guard < 64; guard++)
            {
                var st = from.Stack;
                if (st == null) break;
                var copy = st.Copy();
                var targets = Slots.FindAll(s => s.PlayerInv);
                if (!CanInsertAll(copy, targets)) break;
                Insert(copy, targets);
                from.OnTake?.Invoke(st);
                Changed();
            }
        }

        private static bool CanInsertAll(ItemStack s, List<Slot> targets)
        {
            int left = s.Count;
            foreach (var t in targets)
            {
                var st = t.Stack;
                if (st == null) left -= s.MaxStack;
                else if (st.SameItem(s)) left -= st.MaxStack - st.Count;
                if (left <= 0) return true;
            }
            return false;
        }

        protected static void Insert(ItemStack st, IEnumerable<Slot> targets)
        {
            foreach (var t in targets)
            {
                if (st.Count <= 0) return;
                var ts = t.Stack;
                if (ts != null && ts.SameItem(st) && t.CanPlace(st))
                {
                    int n = Mathf.Min(st.Count, Mathf.Min(t.Max, ts.MaxStack) - ts.Count);
                    if (n > 0) { ts.Count += n; st.Count -= n; t.Stack = ts; }
                }
            }
            foreach (var t in targets)
            {
                if (st.Count <= 0) return;
                if (t.Stack == null && t.CanPlace(st))
                {
                    int n = Mathf.Min(st.Count, Mathf.Min(t.Max, st.MaxStack));
                    t.Stack = st.Copy(n);
                    st.Count -= n;
                }
            }
        }

        private void SwapHotbar(Slot slot, int i)
        {
            if (slot.Output) { var st = slot.Stack; if (st != null && Inventory.Main[i] == null) { Inventory.Main[i] = st.Copy(); slot.OnTake?.Invoke(st); } return; }
            var a = slot.Stack; var b = Inventory.Main[i];
            if (b != null && !slot.CanPlace(b)) return;
            slot.Stack = b; Inventory.Main[i] = a;
            Changed();
        }

        private void SwapOffhand(Slot slot)
        {
            if (slot.Output) return;
            var a = slot.Stack; var b = Inventory.Offhand;
            if (b != null && !slot.CanPlace(b)) return;
            slot.Stack = b; Inventory.Offhand = a;
            Changed();
        }

        // ------------------------------------------------------------------------------------------------ drawing

        public void Draw()
        {
            int w = McGui.ScaledWidth, h = McGui.ScaledHeight;
            McGui.Fill(0, 0, w, h, new Color(0.0627f, 0.0627f, 0.0627f, 0.75f)); // renderBackground gradient (simplified)
            float mx = Input.mousePosition.x / McGui.Scale, my = (Screen.height - Input.mousePosition.y) / McGui.Scale;
            DrawBackground(mx, my);
            DrawExtra(mx, my);
            if (Title != null) McFont.Draw(Title, Left + 8, Top + 6, new Color(0.25f, 0.25f, 0.25f), false);
            if (ShowPlayerInvTitle && HasPlayerInventory()) McFont.Draw(McData.T("container.inventory"), Left + 8, Top + H - 94, new Color(0.25f, 0.25f, 0.25f), false);
            foreach (var s in Slots)
            {
                var st = s.Stack;
                if (drag.Contains(s) && Inventory.Cursor != null && drag.Count > 1)
                {
                    McGui.Fill(Left + s.X, Top + s.Y, 16, 16, new Color(1, 1, 1, 0.5f));
                }
                Hud.DrawItem(st, Left + s.X, Top + s.Y);
            }
            if (hovered != null) McGui.Fill(Left + hovered.X, Top + hovered.Y, 16, 16, new Color(1, 1, 1, 0.5f));
            if (Inventory.Cursor != null) Hud.DrawItem(Inventory.Cursor, mx - 8, my - 8);
            else if (hovered != null && hovered.Stack != null) Tooltip.Draw(hovered.Stack, mx, my);
            else DrawTooltipsExtra(mx, my);
        }

        protected virtual void DrawTooltipsExtra(float mx, float my) { }

        private bool HasPlayerInventory() { foreach (var s in Slots) if (s.PlayerInv) return true; return false; }

        protected virtual void DrawBackground(float mx, float my)
        {
            if (Background != null) McGui.Blit(McTex.Get(Background), Left, Top, 0, 0, W, H, BgW, BgH);
        }

        /// <summary>Items left in temporary grids (crafting) go back to the player, or fall out when there is no room.</summary>
        protected static void ReturnToPlayer(ItemStack[] grid)
        {
            for (int i = 0; i < grid.Length; i++)
            {
                var s = grid[i];
                if (s == null) continue;
                int took = Inventory.Add(s);
                if (took < s.Count) { s.Count -= took; Interaction.Throw(s); }
                grid[i] = null;
            }
        }
    }

    /// <summary>Item tooltips like Minecraft's: name in its rarity colour, enchantments, potion effects, durability, attributes.</summary>
    public static class Tooltip
    {
        public static List<string> Lines(ItemStack s)
        {
            var l = new List<string>();
            var c = s.NameColor;
            string code = c == Color.white ? "§f" : c.b > 0.9f && c.r < 0.5f ? "§b" : c.r > 0.9f && c.b > 0.9f ? "§d" : "§e";
            l.Add(code + s.Name);
            if (s.IsSn) { l.Add("§9Subnautica"); if (Crafting.SnToMc.TryGetValue(s.Tech, out var mc)) l.Add("§7= " + McData.Item(mc)?.Name); return l; }
            if (s.Enchanted)
                foreach (var kv in s.Ench)
                {
                    var name = McData.Enchants.TryGetValue(kv.Key, out var e) ? e.Name : kv.Key;
                    bool curse = e != null && e.Curse;
                    l.Add((curse ? "§c" : "§7") + name + (e != null && e.Max == 1 ? "" : " " + Roman(kv.Value)));
                }
            if (s.Potion != null && McData.Potions.TryGetValue(s.Potion, out var effs))
            {
                if (effs.Count == 0) l.Add("§7" + McData.T("effect.none", "Aucun effet"));
                foreach (var e in effs)
                {
                    var def = McData.Effects[e.Effect];
                    string dur = def.Instant ? "" : string.Format(" ({0}:{1:00})", e.Duration / 1200, e.Duration / 20 % 60);
                    l.Add((def.Category == "harmful" ? "§c" : "§9") + def.Name + (e.Amplifier > 0 ? " " + Roman(e.Amplifier + 1) : "") + dur);
                }
            }
            if (s.Is("firework_rocket") && s.Flight > 0) l.Add("§7" + McData.T("item.minecraft.firework_rocket.flight", "Durée de vol :") + " " + s.Flight);
            if (s.Contents != null) { int n = 0; foreach (var x in s.Contents) if (x != null) { if (n++ < 5) l.Add("§f" + x.Name + " x" + x.Count); } if (n > 5) l.Add("§7§o... (" + (n - 5) + ")"); }
            if (s.Item.AttackDamage > 1f || s.Item.AttackSpeed != 4f)
            {
                l.Add("");
                l.Add("§7" + McData.T("item.modifiers.mainhand", "Dans la main principale :"));
                l.Add("§2 " + Fmt(s.Item.AttackDamage) + " " + McData.T("attribute.name.generic.attack_damage", "Dégâts d'attaque"));
                l.Add("§2 " + Fmt(s.Item.AttackSpeed) + " " + McData.T("attribute.name.generic.attack_speed", "Vitesse d'attaque"));
            }
            if (s.Item.Armor > 0)
            {
                l.Add("");
                l.Add("§7" + McData.T("item.modifiers." + s.Item.ArmorSlot, "Équipé :"));
                l.Add("§9+" + s.Item.Armor + " " + McData.T("attribute.name.generic.armor", "Armure"));
                if (s.Item.Toughness > 0) l.Add("§9+" + Fmt(s.Item.Toughness) + " " + McData.T("attribute.name.generic.armor_toughness", "Robustesse d'armure"));
            }
            if (s.Damageable && s.Damage > 0) l.Add("§f" + McData.Tf("item.durability", s.Item.Durability - s.Damage, s.Item.Durability));
            if (s.Item.Food != null) l.Add("§7+" + s.Item.Food.Hunger + " §6🍖");
            return l;
        }

        private static string Fmt(float f) => f.ToString(f == Mathf.Round(f) ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture);

        public static string Roman(int n)
        {
            string[] r = { "", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X" };
            return n >= 0 && n < r.Length ? r[n] : n.ToString();
        }

        public static void Draw(ItemStack s, float mx, float my) => DrawLines(Lines(s), mx, my);

        /// <summary>TooltipBackgroundRenderer: dark purple-black box with the purple gradient border.</summary>
        public static void DrawLines(List<string> lines, float mx, float my)
        {
            if (lines.Count == 0) return;
            float w = 0;
            foreach (var l in lines) w = Mathf.Max(w, McFont.Width(l));
            float h = lines.Count == 1 ? 8 : 10 + (lines.Count - 1) * 10;
            float x = mx + 12, y = my - 12;
            if (x + w > McGui.ScaledWidth - 4) x = mx - 16 - w;
            if (y + h + 6 > McGui.ScaledHeight) y = McGui.ScaledHeight - h - 6;
            var bg = new Color(0.063f, 0, 0.063f, 0.94f);
            McGui.Fill(x - 3, y - 4, w + 6, 1, bg); McGui.Fill(x - 3, y + h + 3, w + 6, 1, bg);
            McGui.Fill(x - 3, y - 3, w + 6, h + 6, bg); McGui.Fill(x - 4, y - 3, 1, h + 6, bg); McGui.Fill(x + w + 3, y - 3, 1, h + 6, bg);
            var b1 = new Color(0.314f, 0, 1f, 0.31f); var b2 = new Color(0.157f, 0, 0.5f, 0.31f);
            McGui.Fill(x - 3, y - 3 + 1, 1, h + 4, b1); McGui.Fill(x + w + 2, y - 3 + 1, 1, h + 4, b1);
            McGui.Fill(x - 3, y - 3, w + 6, 1, b1); McGui.Fill(x - 3, y + h + 2, w + 6, 1, b2);
            float yy = y;
            for (int i = 0; i < lines.Count; i++)
            {
                McFont.Draw(lines[i], x, yy, Color.white);
                yy += i == 0 ? 12 : 10;
            }
        }
    }

    /// <summary>Opens / closes screens and routes input to the open one.</summary>
    public static class Screens
    {
        public static McScreen Current;
        public static bool Open => Current != null;
        public static bool HideHand => Current != null && Current.HideHand;
        public static bool PausesGame => false;
        private static int closedFrame = -10;
        public static bool JustClosed => Time.frameCount - closedFrame <= 1;

        public static void Show(McScreen s)
        {
            if (Current != null) Close();
            Current = s;
            s.OnOpen();
            Interaction.StopMining();
            Interaction.StopUsing(false);
            UWE.Utils.lockCursor = false;
        }

        public static void Close()
        {
            if (Current == null) return;
            var s = Current;
            Current = null;
            s.OnClose();
            if (Inventory.Cursor != null)
            {
                int took = Inventory.Add(Inventory.Cursor);
                if (took < Inventory.Cursor.Count) { Inventory.Cursor.Count -= took; Interaction.Throw(Inventory.Cursor); }
                Inventory.Cursor = null;
            }
            closedFrame = Time.frameCount;
            UWE.Utils.lockCursor = true;
        }

        public static void Update()
        {
            if (Current == null) return;
            UWE.Utils.lockCursor = false;
            if (Input.GetKeyDown(KeyCode.Escape) || (Input.GetKeyDown(KeyCode.E) && !(Current is CreativeScreen cs && cs.Typing) && !(Current is AnvilScreen an && an.Typing))) { Close(); return; }
            Current.Update();
        }

        public static void Draw() { if (Current != null) Current.Draw(); }

        public static void Toggle()
        {
            if (Open) Close();
            else Show(GameMode.Creative ? (McScreen)new CreativeScreen() : new InventoryScreen());
        }

        public static int HeldGridCount(TechType t)
        {
            int n = 0;
            if (Current != null) foreach (var s in Current.TemporaryStacks()) if (s != null && s.Tech == t) n += s.Count;
            return n;
        }

        public static void ForEachGridItem(Action<ItemStack> f)
        {
            if (Current != null) foreach (var s in Current.TemporaryStacks()) f(s);
        }

        public static bool TakeFromGrids(TechType t)
        {
            if (Current == null) return false;
            foreach (var s in Current.TemporaryStacks())
                if (s != null && s.Tech == t && s.Count > 0) { s.Count--; return true; }
            return false;
        }

        /// <summary>Right click on a Minecraft block that has a screen.</summary>
        public static bool OpenFor(Vector3Int c, BState s)
        {
            switch (s.Block.Class)
            {
                case "CraftingTableBlock": Show(new CraftingScreen()); return true;
                case "FurnaceBlock": Show(new FurnaceScreen(c, "smelting", "gui/container/furnace", s.Block.Name)); return true;
                case "BlastFurnaceBlock": Show(new FurnaceScreen(c, "blasting", "gui/container/blast_furnace", s.Block.Name)); return true;
                case "SmokerBlock": Show(new FurnaceScreen(c, "smoking", "gui/container/smoker", s.Block.Name)); return true;
                case "ChestBlock": case "TrappedChestBlock": case "BarrelBlock":
                    Show(new ChestScreen(c, s, BlockWorld.Entity(c, true, 27).Items, s.Block.Name)); return true;
                case "EnderChestBlock": Show(new ChestScreen(c, s, Inventory.EnderChest, s.Block.Name)); return true;
                case "ShulkerBoxBlock": Show(new ChestScreen(c, s, BlockWorld.Entity(c, true, 27).Items, s.Block.Name, true)); return true;
                case "DispenserBlock": case "DropperBlock": Show(new ChestScreen(c, s, BlockWorld.Entity(c, true, 9).Items, s.Block.Name)); return true;
                case "HopperBlock": Show(new ChestScreen(c, s, BlockWorld.Entity(c, true, 5).Items, s.Block.Name)); return true;
                case "EnchantingTableBlock": Show(new EnchantingScreen(c)); return true;
                case "AnvilBlock": Show(new AnvilScreen(c)); return true;
                case "BrewingStandBlock": Show(new BrewingScreen(c)); return true;
                case "StonecutterBlock": Show(new StonecutterScreen()); return true;
                case "SmithingTableBlock": Show(new SmithingScreen()); return true;
                case "GrindstoneBlock": Show(new GrindstoneScreen()); return true;
            }
            return false;
        }
    }

    // ==================================================================================================== screens

    /// <summary>The survival inventory (PlayerScreenHandler): armour, offhand, 2x2 crafting, Amai's preview.</summary>
    public class InventoryScreen : McScreen
    {
        private readonly ItemStack[] grid = new ItemStack[4];
        private ItemStack result;
        public override bool ShowPlayerInvTitle => false;

        public InventoryScreen()
        {
            Background = "gui/container/inventory";
            for (int i = 0; i < 4; i++)
            {
                int ai = 3 - i;
                string slot = ai == 3 ? "head" : ai == 2 ? "chest" : ai == 1 ? "legs" : "feet";
                Slots.Add(new Slot { X = 8, Y = 8 + i * 18, Get = () => Inventory.Armor[ai], Set = s => { if (s != null && Inventory.Armor[ai] == null) Sounds.Play("item.armor.equip_generic", null); Inventory.Armor[ai] = s; }, Max = 1, Group = 3,
                    CanPlace = s => s.IsMc && (s.Item.ArmorSlot == slot || slot == "chest" && s.Is("elytra") || slot == "head" && (s.Item.Block != null && (s.Item.Block.Class.Contains("Skull") || s.Item.Id == "carved_pumpkin"))) });
            }
            Slots.Add(new Slot { X = 77, Y = 62, Get = () => Inventory.Offhand, Set = s => Inventory.Offhand = s, Group = 4 });
            for (int y = 0; y < 2; y++)
                for (int x = 0; x < 2; x++)
                {
                    int i = y * 2 + x;
                    Slots.Add(new Slot { X = 98 + x * 18, Y = 18 + y * 18, Get = () => grid[i], Set = s => { grid[i] = s; Update2(); } });
                }
            Slots.Add(new Slot { X = 154, Y = 28, Output = true, Get = () => result, Set = s => result = s, OnTake = Take });
            AddPlayerInventory(8, 84);
        }

        public override IEnumerable<ItemStack> TemporaryStacks() { foreach (var s in grid) yield return s; }

        private void Update2() { var r = Crafting.Craft(grid, 2, 2); result = r?.Output; }
        protected override void Changed() => Update2();

        private void Take(ItemStack taken)
        {
            Crafting.Consume(grid);
            foreach (var s in grid) if (s != null && s.IsSn && !SnBridge.IsVirtual(s.Tech)) { }
            Update2();
        }

        public override void OnClose() => ReturnToPlayer(grid);

        protected override void DrawBackground(float mx, float my)
        {
            base.DrawBackground(mx, my);
            McFont.Draw(McData.T("container.crafting"), Left + 97, Top + 8, new Color(0.25f, 0.25f, 0.25f), false);
            InventoryPreview.Draw(Left + 26, Top + 8, 49, 70, mx, my);
        }
    }

    /// <summary>Amai turning to look at the mouse in the inventory, drawn by rendering the body to a texture.</summary>
    public static class InventoryPreview
    {
        private static RenderTexture rt;
        private static Camera cam;
        private static GameObject stage;
        private static PlayerBody body;

        public static void Draw(float x, float y, float w, float h, float mx, float my)
        {
            if (Event.current.type != EventType.Repaint || View.I == null) return;
            if (rt == null) rt = new RenderTexture(128, 192, 16, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Point };
            if (stage == null)
            {
                stage = new GameObject("SubnautiCraft preview stage");
                UnityEngine.Object.DontDestroyOnLoad(stage);
                stage.transform.position = new Vector3(0, -21000, 0);
                var cg = new GameObject("cam"); cg.transform.SetParent(stage.transform, false);
                cg.transform.localPosition = new Vector3(0, 0.95f, 4f);
                cg.transform.localRotation = Quaternion.Euler(0, 180, 0);
                cam = cg.AddComponent<Camera>();
                cam.orthographic = true; cam.orthographicSize = 1.15f;
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0, 0, 0, 0);
                cam.cullingMask = 1 << 28; cam.enabled = false; cam.targetTexture = rt;
                var lg = new GameObject("light"); lg.transform.SetParent(stage.transform, false);
                lg.transform.rotation = Quaternion.Euler(30, 150, 0);
                var l = lg.AddComponent<Light>(); l.type = LightType.Directional; l.intensity = 1f; l.cullingMask = 1 << 28;
                body = PlayerBody.Create(McTex.Get("../amai"), true);
                body.transform.SetParent(stage.transform, false);
                foreach (var t in body.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 28;
            }
            // InventoryScreen.drawEntity: yaw follows the mouse, head pitch too
            float yawOff = Mathf.Atan((x + w / 2 - mx) / 40f) * 40f, pitchOff = Mathf.Atan((y + h * 0.3f - my) / 40f) * 20f;
            body.transform.localPosition = Vector3.zero;
            body.transform.localRotation = Quaternion.Euler(0, -yawOff * 0.5f, 0);
            foreach (var r in body.GetComponentsInChildren<Renderer>(true)) { r.enabled = true; r.gameObject.layer = 28; }
            var amb = RenderSettings.ambientLight; var mode = RenderSettings.ambientMode;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat; RenderSettings.ambientLight = new Color(0.6f, 0.6f, 0.6f);
            cam.Render();
            RenderSettings.ambientLight = amb; RenderSettings.ambientMode = mode;
            GUI.DrawTexture(new Rect(x * McGui.Scale, (y - 6) * McGui.Scale, w * McGui.Scale, (h + 6) * McGui.Scale), rt, ScaleMode.ScaleToFit, true);
        }
    }

    /// <summary>CraftingScreenHandler: the 3x3 grid.</summary>
    public class CraftingScreen : McScreen
    {
        private readonly ItemStack[] grid = new ItemStack[9];
        private ItemStack result;

        public CraftingScreen()
        {
            Background = "gui/container/crafting_table";
            Title = McData.T("container.crafting");
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 3; x++)
                {
                    int i = y * 3 + x;
                    Slots.Add(new Slot { X = 30 + x * 18, Y = 17 + y * 18, Get = () => grid[i], Set = s => { grid[i] = s; Recompute(); } });
                }
            Slots.Add(new Slot { X = 124, Y = 35, Output = true, Get = () => result, Set = s => result = s, OnTake = t => { Crafting.Consume(grid); Recompute(); } });
            AddPlayerInventory(8, 84);
        }

        public override IEnumerable<ItemStack> TemporaryStacks() { foreach (var s in grid) yield return s; }
        private void Recompute() { result = Crafting.Craft(grid, 3, 3)?.Output; }
        protected override void Changed() => Recompute();
        public override void OnClose() => ReturnToPlayer(grid);
    }

    /// <summary>Furnaces, blast furnaces and smokers (AbstractFurnaceScreenHandler); the cooking itself runs in Machines.</summary>
    public class FurnaceScreen : McScreen
    {
        private readonly Vector3Int cell;
        private readonly BlockEntity e;
        private readonly string type;

        public FurnaceScreen(Vector3Int c, string kind, string bg, string title)
        {
            cell = c; type = kind; Background = bg; Title = title;
            e = BlockWorld.Entity(c, true, 3);
            if (e.Items == null) e.Items = new ItemStack[3];
            Slots.Add(new Slot { X = 56, Y = 17, Get = () => e.Items[0], Set = s => e.Items[0] = s });
            Slots.Add(new Slot { X = 56, Y = 53, Get = () => e.Items[1], Set = s => e.Items[1] = s, CanPlace = s => Crafting.FuelTime(s) > 0 || s.Is("bucket") });
            Slots.Add(new Slot { X = 116, Y = 35, Get = () => e.Items[2], Set = s => e.Items[2] = s, CanPlace = s => false, OnTake = t => TakeXp() });
            Slots[2].Output = false;
            AddPlayerInventory(8, 84);
        }

        private void TakeXp()
        {
            int xp = Mathf.FloorToInt(e.Xp);
            if (UnityEngine.Random.value < e.Xp - xp) xp++;
            e.Xp = 0;
            if (xp > 0) XpOrb.Spawn(Player.main.transform.position, xp);
        }

        protected override void Click(Slot slot, int button)
        {
            bool output = slot == Slots[2];
            bool had = output && slot.Stack != null;
            base.Click(slot, button);
            if (had) TakeXp();
        }

        protected override void DrawExtra(float mx, float my)
        {
            var bg = McTex.Get(Background);
            if (e.Burn > 0)
            {
                int k = Mathf.CeilToInt(e.Burn * 13f / Mathf.Max(1, e.BurnMax));
                McGui.Blit(bg, Left + 56, Top + 36 + 12 - k, 176, 12 - k, 14, k + 1);
            }
            int l = e.CookMax > 0 ? e.Cook * 24 / e.CookMax : 0;
            McGui.Blit(bg, Left + 79, Top + 34, 176, 14, l + 1, 16);
        }
    }

    /// <summary>Chests, barrels, shulker boxes, the ender chest, dispensers and hoppers (GenericContainerScreenHandler).</summary>
    public class ChestScreen : McScreen
    {
        private readonly Vector3Int cell;
        private readonly BState state;
        private readonly ItemStack[] items;
        private readonly int rows;
        private readonly bool shulker;

        public ChestScreen(Vector3Int c, BState s, ItemStack[] it, string title, bool isShulker = false)
        {
            cell = c; state = s; items = it; Title = title; shulker = isShulker;
            rows = Mathf.Max(1, it.Length / 9);
            if (it.Length == 5)
            {
                Background = "gui/container/hopper"; H = 133;
                for (int i = 0; i < 5; i++) { int k = i; Slots.Add(new Slot { X = 44 + i * 18, Y = 20, Get = () => items[k], Set = v => items[k] = v, CanPlace = Storable }); }
                AddPlayerInventory(8, 51);
            }
            else if (it.Length == 9)
            {
                Background = "gui/container/dispenser";
                for (int i = 0; i < 9; i++) { int k = i; Slots.Add(new Slot { X = 62 + (i % 3) * 18, Y = 17 + (i / 3) * 18, Get = () => items[k], Set = v => items[k] = v, CanPlace = Storable }); }
                AddPlayerInventory(8, 84);
            }
            else
            {
                Background = shulker ? "gui/container/shulker_box" : "gui/container/generic_54";
                H = 114 + rows * 18;
                for (int r = 0; r < rows; r++)
                    for (int col = 0; col < 9; col++)
                    {
                        int k = r * 9 + col;
                        Slots.Add(new Slot { X = 8 + col * 18, Y = 18 + r * 18, Get = () => items[k], Set = v => items[k] = v, CanPlace = v => (!shulker || !(v.IsMc && v.Item.Id.EndsWith("shulker_box"))) && Storable(v) });
                    }
                AddPlayerInventory(8, 18 + rows * 18 + 13);
            }
        }

        /// <summary>Subnautica tools and equipment keep their charge and state only in Subnautica's own storage, so they stay out of Minecraft chests.</summary>
        public static bool Storable(ItemStack v) => !(v.IsSn && !SnBridge.IsVirtual(v.Tech));

        public override void OnOpen()
        {
            var e = BlockWorld.Entity(cell);
            if (e != null) e.LidTarget = 1f;
            var cls = state.Block.Class;
            Sounds.Play(cls == "BarrelBlock" ? "block.barrel.open" : cls == "ShulkerBoxBlock" ? "block.shulker_box.open" : cls == "EnderChestBlock" ? "block.ender_chest.open" : "block.chest.open", BlockWorld.Center(cell), 0.5f, UnityEngine.Random.value * 0.1f + 0.9f);
            if (cls == "BarrelBlock") BlockWorld.Set(cell, state.With("open", "true"), false);
        }

        public override void OnClose()
        {
            var e = BlockWorld.Entity(cell);
            if (e != null) e.LidTarget = 0f;
            var cls = state.Block.Class;
            Sounds.Play(cls == "BarrelBlock" ? "block.barrel.close" : cls == "ShulkerBoxBlock" ? "block.shulker_box.close" : cls == "EnderChestBlock" ? "block.ender_chest.close" : "block.chest.close", BlockWorld.Center(cell), 0.5f, UnityEngine.Random.value * 0.1f + 0.9f);
            if (cls == "BarrelBlock") { var now = BlockWorld.Get(cell); if (now != null && now.Block == state.Block) BlockWorld.Set(cell, now.With("open", "false"), false); }
            // Subnautica tools stored in a Minecraft chest leave Subnautica's inventory (their real object goes)
            Persist.Dirty = true;
        }

        protected override void DrawBackground(float mx, float my)
        {
            var tex = McTex.Get(Background);
            if (Background.EndsWith("generic_54"))
            {
                McGui.Blit(tex, Left, Top, 0, 0, W, rows * 18 + 17);
                McGui.Blit(tex, Left, Top + rows * 18 + 17, 0, 126, W, 96);
            }
            else base.DrawBackground(mx, my);
        }
    }

    /// <summary>
    /// A Subnautica locker, wall locker or any StorageContainer shown as a Minecraft chest: its items grouped by kind.
    /// Putting a Subnautica item in moves (or spawns) the real object into the locker; taking one out brings it back
    /// to the player like any pickup. Minecraft items stay out (Subnautica's lockers only hold Subnautica things).
    /// </summary>
    public class SnStorageScreen : McScreen
    {
        private readonly StorageContainer storage;
        private readonly ItemStack[] view;
        private readonly int rows;

        public SnStorageScreen(StorageContainer sc)
        {
            storage = sc;
            Title = Language.main.Get(sc.storageLabel);
            int cells = sc.container.sizeX * sc.container.sizeY;
            rows = Mathf.Clamp(Mathf.CeilToInt(cells / 9f), 1, 6);
            view = new ItemStack[rows * 9];
            Background = "gui/container/generic_54";
            H = 114 + rows * 18;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < 9; c++)
                {
                    int k = r * 9 + c;
                    Slots.Add(new Slot { X = 8 + c * 18, Y = 18 + r * 18, Get = () => view[k], Set = v => SetSlot(k, v), CanPlace = v => v.IsSn && storage.container.HasRoomFor(v.Tech) });
                }
            AddPlayerInventory(8, 18 + rows * 18 + 13);
            Refresh();
        }

        private void Refresh()
        {
            for (int i = 0; i < view.Length; i++) view[i] = null;
            int k = 0;
            foreach (var t in storage.container.GetItemTypes())
            {
                int n = storage.container.GetCount(t);
                while (n > 0 && k < view.Length)
                {
                    int m = Mathf.Min(n, SnItems.MaxStack(t));
                    view[k++] = new ItemStack(t, m);
                    n -= m;
                }
            }
        }

        private void SetSlot(int k, ItemStack v)
        {
            var before = view[k];
            int had = before != null ? before.Count : 0;
            var tech = v != null ? v.Tech : before != null ? before.Tech : TechType.None;
            int now = v != null ? v.Count : 0;
            int diff = now - (before != null && v != null && before.Tech == v.Tech ? had : 0);
            if (before != null && (v == null || before.Tech != v.Tech))
            {
                // taken out: real objects come out of the locker into the player's possession
                for (int i = 0; i < had; i++)
                {
                    var p = storage.container.RemoveItem(before.Tech);
                    if (p == null) break;
                    if (SnBridge.IsVirtual(before.Tech)) UnityEngine.Object.Destroy(p.gameObject);
                    else { SnBridge.Suspend = true; global::Inventory.main.container.UnsafeAdd(new InventoryItem(p)); SnBridge.Suspend = false; }
                }
                diff = now;
            }
            if (v != null && diff > 0) UWE.CoroutineHost.StartCoroutine(Store(v.Tech, diff));
            else if (v != null && diff < 0)
                for (int i = 0; i < -diff; i++) { var p = storage.container.RemoveItem(v.Tech); if (p != null) { if (SnBridge.IsVirtual(v.Tech)) UnityEngine.Object.Destroy(p.gameObject); else global::Inventory.main.container.UnsafeAdd(new InventoryItem(p)); } }
            view[k] = v;
        }

        private System.Collections.IEnumerator Store(TechType t, int n)
        {
            for (int i = 0; i < n; i++)
            {
                if (!SnBridge.IsVirtual(t))
                {
                    var p = global::Inventory.main.container.RemoveItem(t);
                    if (p != null) { storage.container.AddItem(p); continue; }
                }
                var task = CraftData.GetPrefabForTechTypeAsync(t);
                yield return task;
                var prefab = task.GetResult();
                if (prefab == null) yield break;
                var go = UnityEngine.Object.Instantiate(prefab);
                var pk = go.GetComponent<Pickupable>();
                if (pk == null) { UnityEngine.Object.Destroy(go); yield break; }
                pk.Pickup(false);
                storage.container.AddItem(pk);
            }
        }

        public override void OnOpen() { Sounds.Play("block.chest.open", storage.transform.position, 0.5f); }
        public override void OnClose() { Sounds.Play("block.chest.close", storage.transform.position, 0.5f); }

        protected override void DrawBackground(float mx, float my)
        {
            var tex = McTex.Get(Background);
            McGui.Blit(tex, Left, Top, 0, 0, W, rows * 18 + 17);
            McGui.Blit(tex, Left, Top + rows * 18 + 17, 0, 126, W, 96);
        }
    }
}
