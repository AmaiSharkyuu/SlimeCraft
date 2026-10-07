using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Minecraft's screens over Slime Rancher:
    ///  - E: the creative inventory. Creative mode has every tab (building, colored, natural, functional, and a
    ///    Slime Rancher tab with every slime, largo, plort, food, chicken and toy); survival only gets a small free
    ///    palette of plain blocks. The bottom-right tab is the survival inventory with Steve following the mouse.
    ///  - Right click on a chest, barrel or shulker box: its 27 slots above the player's inventory.
    /// Slot clicks follow Minecraft (left takes/places/swaps, right halves/places one, shift moves, 1-9 swaps with
    /// the hotbar, A/Q drops, clicking outside throws).
    /// </summary>
    public static class Screens
    {
        public static bool Open;
        public static bool HideHand => false;

        private class Tab
        {
            public string Key, Fallback;
            public System.Func<List<ItemStack>> Items;
            public ItemStack Icon;
            public bool Bottom;
            public bool Storage;
            public int Column;
            private List<ItemStack> cache;
            public List<ItemStack> List => cache ?? (cache = Items != null ? Items() : new List<ItemStack>());
            public string Title => McAssets.T(Key, Fallback);
        }

        private static List<Tab> tabs;
        private static bool tabsCreative;
        private static int tab;
        private static float scroll;
        private static bool draggingScroll;
        private static int left, top;
        private static int W = 195, H = 136;
        private static vp_FPInput fpInput;
        private static Blocks.Placed container;

        // Steve preview
        private static RenderTexture rt;
        private static Camera previewCam;
        private static SteveBody previewSteve;
        private static GameObject previewRig;

        // ------------------------------------------------------------------------------------------------ tabs

        private static ItemStack B(string id) => Blocks.ById.TryGetValue(id, out var d) ? new ItemStack(d, 1) : null;

        private static List<ItemStack> BlocksOfTab(string t)
        {
            var l = new List<ItemStack>();
            foreach (var d in Blocks.Defs) if (d.Tab == t) l.Add(new ItemStack(d, 1));
            return l;
        }

        private static List<ItemStack> SurvivalPalette()
        {
            var l = new List<ItemStack>();
            foreach (var d in Blocks.Defs) if (d.Survival) l.Add(new ItemStack(d, 1));
            return l;
        }

        /// <summary>Every Slime Rancher thing the vacpack can hold: slimes, largos, plorts, food, chickens, toys.</summary>
        private static List<ItemStack> SlimeRancherItems()
        {
            var l = new List<ItemStack>();
            var look = SRSingleton<GameContext>.Instance.LookupDirector;
            var groups = new List<ItemStack>[5];
            for (int i = 0; i < groups.Length; i++) groups[i] = new List<ItemStack>();
            foreach (Identifiable.Id id in System.Enum.GetValues(typeof(Identifiable.Id)))
            {
                if (id == Identifiable.Id.NONE || Identifiable.IsGordo(id) || Identifiable.IsLiquid(id)) continue;
                GameObject prefab = null;
                Sprite icon = null;
                try { prefab = look.GetPrefab(id); icon = look.GetIcon(id); } catch { }
                if (prefab == null || icon == null || prefab.GetComponent<Vacuumable>() == null) continue;
                if (icon.name.ToLowerInvariant().Contains("needed")) continue; // Slime Rancher's "IMAGE NEEDED" placeholder
                int g = Identifiable.IsLargo(id) ? 1 : Identifiable.IsSlime(id) ? 0 : Identifiable.IsPlort(id) ? 2 : Identifiable.IsFood(id) || Identifiable.IsAnimal(id) ? 3 : 4;
                groups[g].Add(new ItemStack(id, 1));
            }
            foreach (var g in groups) l.AddRange(g);
            return l;
        }

        private static void EnsureTabs()
        {
            if (tabs != null && tabsCreative == GameMode.Creative) return;
            tabsCreative = GameMode.Creative;
            tab = 0; scroll = 0;
            tabs = new List<Tab>();
            if (GameMode.Creative)
            {
                tabs.Add(new Tab { Key = "itemGroup.buildingBlocks", Fallback = "Building Blocks", Items = () => BlocksOfTab("building"), Icon = B("bricks"), Column = 0 });
                tabs.Add(new Tab { Key = "itemGroup.coloredBlocks", Fallback = "Colored Blocks", Items = () => BlocksOfTab("colored"), Icon = B("cyan_wool"), Column = 1 });
                tabs.Add(new Tab { Key = "itemGroup.natural", Fallback = "Natural Blocks", Items = () => BlocksOfTab("natural"), Icon = B("grass_block"), Column = 2 });
                tabs.Add(new Tab { Key = "itemGroup.functional", Fallback = "Functional Blocks", Items = () => BlocksOfTab("functional"), Icon = B("chest"), Column = 3 });
                tabs.Add(new Tab { Key = "sr", Fallback = "Slime Rancher", Items = SlimeRancherItems, Icon = new ItemStack(Identifiable.Id.PINK_SLIME, 1), Column = 4 });
            }
            else
                tabs.Add(new Tab { Key = "itemGroup.buildingBlocks", Fallback = "Building Blocks", Items = SurvivalPalette, Icon = B("bricks"), Column = 0 });
            // Slime storage: a pen in the inventory where slimes wait safe and warm, in both modes.
            tabs.Add(new Tab { Key = "slimeStorage", Fallback = "Stockage de Slime", Icon = new ItemStack(Identifiable.Id.TABBY_SLIME, 1), Storage = true, Column = GameMode.Creative ? 5 : 1 });
            tabs.Add(new Tab { Key = "itemGroup.inventory", Fallback = "Survival Inventory", Icon = B("crafting_table"), Bottom = true, Column = 6 });
        }

        private static bool InventoryTab => tabs[tab].Bottom;
        private static bool StorageTab => container == null && tabs != null && tabs[tab].Storage;

        // ------------------------------------------------------------------------------------------------ open/close

        public static void Toggle()
        {
            if (Open) Close(); else Show();
        }

        public static void Show()
        {
            if (Open) return;
            container = null;
            EnsureTabs();
            Begin();
        }

        public static void OpenContainer(Blocks.Placed p)
        {
            if (Open) return;
            container = p;
            if (p.Contents == null) p.Contents = new ItemStack[27];
            p.LidTarget = 1f;
            var pos = Blocks.Center(p.Cell);
            if (p.Def.Shape == BlockShape.Shulker) McSound.Play("block/shulker_box/open", pos, 0.5f, Random.Range(0.9f, 1f));
            else if (p.Def.Shape == BlockShape.Barrel) McSound.Play("block/barrel/open" + Random.Range(1, 3), pos, 0.5f, Random.Range(0.9f, 1f));
            else McSound.Play("block/chest/open", pos, 0.5f, Random.Range(0.9f, 1f));
            Begin();
        }

        private static void Begin()
        {
            Open = true;
            var player = SRSingleton<SceneContext>.Instance.Player;
            fpInput = player != null ? player.GetComponent<vp_FPInput>() : null;
            if (fpInput != null) { fpInput.MouseCursorForced = true; fpInput.MouseCursorBlocksMouseLook = true; }
            vp_Utility.LockCursor = false;
            Cursor.visible = true;
            if (View.I != null) View.I.UseTicksLeft = 0;
        }

        public static void Close()
        {
            if (!Open) return;
            Open = false;
            if (Inventory.Cursor != null)
            {
                // Minecraft drops whatever the mouse still carries when the screen closes.
                var c = Inventory.Cursor;
                Inventory.Cursor = null;
                int took = Inventory.Add(c);
                c.Count -= took;
                if (c.Count > 0) Interaction.Throw(c);
            }
            if (container != null)
            {
                container.LidTarget = 0f;
                var pos = Blocks.Center(container.Cell);
                if (container.Def.Shape == BlockShape.Shulker) McSound.Play("block/shulker_box/close", pos, 0.5f, Random.Range(0.9f, 1f));
                else if (container.Def.Shape == BlockShape.Barrel) McSound.Play("block/barrel/close", pos, 0.5f, Random.Range(0.9f, 1f));
                else McSound.Play("block/chest/close" + Random.Range(1, 4), pos, 0.5f, Random.Range(0.9f, 1f));
                container = null;
            }
            if (fpInput != null) fpInput.MouseCursorForced = false;
            vp_Utility.LockCursor = true;
            Cursor.visible = false;
            if (previewRig != null) previewRig.SetActive(false);
            Persist.Dirty = true;
        }

        public static void ContainerGone(Blocks.Placed p)
        {
            if (Open && container == p) { container = null; Close(); }
        }

        private static Vector2 Mouse => new Vector2(Input.mousePosition.x / McGui.Scale, (Screen.height - Input.mousePosition.y) / McGui.Scale);

        // ------------------------------------------------------------------------------------------------ slots

        private struct Slot
        {
            public ItemStack[] Arr;
            public int Index;
            public bool Valid => Arr != null;
            public ItemStack Get => Arr[Index];
            public void Set(ItemStack s) { Arr[Index] = s; }
        }

        private static readonly List<KeyValuePair<Rect, Slot>> slotRects = new List<KeyValuePair<Rect, Slot>>();
        private static int paletteHover = -1;
        private static bool destroyHover;

        private static List<ItemStack> Palette => container == null && !InventoryTab && !tabs[tab].Storage ? tabs[tab].List : null;
        private static int PaletteRows => Palette == null ? 0 : Mathf.CeilToInt(Palette.Count / 9f);
        private static int ScrollRow => Mathf.RoundToInt(scroll * Mathf.Max(0, PaletteRows - 5));

        /// <summary>Lays out every slot of the current screen in GUI pixels.</summary>
        private static void Layout()
        {
            slotRects.Clear();
            if (container != null)
            {
                W = 176; H = container.Def.Shape == BlockShape.Shulker ? 166 : 167;
                left = (McGui.ScaledWidth - W) / 2; top = (McGui.ScaledHeight - H) / 2;
                for (int i = 0; i < 27; i++) slotRects.Add(new KeyValuePair<Rect, Slot>(new Rect(left + 8 + i % 9 * 18, top + 18 + i / 9 * 18, 16, 16), new Slot { Arr = container.Contents, Index = i }));
                for (int i = 0; i < 27; i++) slotRects.Add(new KeyValuePair<Rect, Slot>(new Rect(left + 8 + i % 9 * 18, top + 84 + i / 9 * 18, 16, 16), new Slot { Arr = Inventory.Slots, Index = 9 + i }));
                for (int i = 0; i < 9; i++) slotRects.Add(new KeyValuePair<Rect, Slot>(new Rect(left + 8 + i * 18, top + 142, 16, 16), new Slot { Arr = Inventory.Slots, Index = i }));
                return;
            }
            W = 195; H = 136;
            left = (McGui.ScaledWidth - W) / 2; top = (McGui.ScaledHeight - H) / 2;
            for (int i = 0; i < 9; i++) slotRects.Add(new KeyValuePair<Rect, Slot>(new Rect(left + 9 + i * 18, top + 112, 16, 16), new Slot { Arr = Inventory.Slots, Index = i }));
            if (StorageTab)
                for (int i = 0; i < Inventory.SlimeStorage.Length; i++)
                    slotRects.Add(new KeyValuePair<Rect, Slot>(new Rect(left + 9 + i % 9 * 18, top + 18 + i / 9 * 18, 16, 16), new Slot { Arr = Inventory.SlimeStorage, Index = i }));
            if (InventoryTab)
                for (int i = 0; i < 27; i++) slotRects.Add(new KeyValuePair<Rect, Slot>(new Rect(left + 9 + i % 9 * 18, top + 54 + i / 9 * 18, 16, 16), new Slot { Arr = Inventory.Slots, Index = 9 + i }));
        }

        private static Rect Grow(Rect r) => new Rect(r.x - 1, r.y - 1, 18, 18);

        private static Slot HoverSlot(Vector2 m)
        {
            foreach (var kv in slotRects) if (Grow(kv.Key).Contains(m)) return kv.Value;
            return default;
        }

        private static bool Allowed(Slot s, ItemStack item)
        {
            if (item != null && s.Arr == Inventory.SlimeStorage && !(item.IsActor && Identifiable.IsSlime(item.Actor))) return false; // slimes only
            // Shulker boxes cannot go inside shulker boxes, as in Minecraft.
            return item == null || container == null || s.Arr != container.Contents || container.Def.Shape != BlockShape.Shulker
                   || !(item.IsBlock && item.Block.Shape == BlockShape.Shulker);
        }

        // ------------------------------------------------------------------------------------------------ input

        public static void Update()
        {
            if (!Open) return;
            if (container == null) EnsureTabs();
            vp_Utility.LockCursor = false;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            Layout();
            var m = Mouse;

            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.E)) { Close(); return; }

            if (container == null && Input.GetMouseButtonDown(0))
                for (int i = 0; i < tabs.Count; i++)
                    if (TabRect(tabs[i]).Contains(m))
                    {
                        if (tab != i) { tab = i; scroll = 0; McSound.Ui("random/click"); }
                        return;
                    }

            // palette and scrollbar
            paletteHover = -1; destroyHover = false;
            if (Palette != null)
            {
                var bar = new Rect(left + 175, top + 18, 12, 112);
                if (Input.GetMouseButtonDown(0) && bar.Contains(m)) draggingScroll = true;
                if (!Input.GetMouseButton(0)) draggingScroll = false;
                if (draggingScroll) scroll = Mathf.Clamp01((m.y - bar.y - 7.5f) / (bar.height - 15f));
                float wheel = Input.mouseScrollDelta.y;
                if (Mathf.Abs(wheel) > 0.01f && PaletteRows > 5) scroll = Mathf.Clamp01(scroll - Mathf.Sign(wheel) / (PaletteRows - 5));
                for (int row = 0; row < 5; row++)
                    for (int col = 0; col < 9; col++)
                        if (Grow(new Rect(left + 9 + col * 18, top + 18 + row * 18, 16, 16)).Contains(m))
                        {
                            int idx = (ScrollRow + row) * 9 + col;
                            if (idx < Palette.Count) paletteHover = idx;
                        }
            }
            if (container == null && InventoryTab && Grow(new Rect(left + 173, top + 112, 16, 16)).Contains(m)) destroyHover = true;

            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            var hover = HoverSlot(m);

            // number keys swap the hovered slot with a hotbar slot
            for (int k = 0; k < 9; k++)
                if (Input.GetKeyDown(KeyCode.Alpha1 + k))
                {
                    if (hover.Valid && Allowed(hover, Inventory.Slots[k]))
                    {
                        var a = hover.Get; hover.Set(Inventory.Slots[k]); Inventory.Slots[k] = a;
                    }
                    else if (paletteHover >= 0) Inventory.Slots[k] = Palette[paletteHover].Copy(Palette[paletteHover].MaxStack);
                    Persist.Dirty = true;
                }

            if (Input.GetKeyDown(Interaction.DropKey) && hover.Valid && hover.Get != null)
            {
                bool all = Input.GetKey(KeyCode.LeftControl);
                var s = hover.Get;
                var one = s.Copy(all ? s.Count : 1);
                s.Count -= one.Count;
                if (s.Count <= 0) hover.Set(null);
                Interaction.Throw(one);
            }

            bool l = Input.GetMouseButtonDown(0), r = Input.GetMouseButtonDown(1);
            if (!l && !r) return;
            bool inPanel = new Rect(left, top, W, H).Contains(m);
            if (container == null) foreach (var t in tabs) inPanel |= TabRect(t).Contains(m);

            if (destroyHover)
            {
                if (Inventory.Cursor != null) Inventory.Cursor = null;
                else if (shift) for (int i = 0; i < 36; i++) Inventory.Slots[i] = null;
                McSound.Ui("random/click");
                return;
            }
            if (paletteHover >= 0)
            {
                var item = Palette[paletteHover];
                if (Inventory.Cursor != null && !Inventory.Cursor.SameItem(item)) Inventory.Cursor = null; // dropping into the palette deletes, like Minecraft
                else if (shift) Inventory.Add(item.Copy(item.MaxStack));
                else if (Inventory.Cursor != null) { if (Inventory.Cursor.Count < Inventory.Cursor.MaxStack) Inventory.Cursor.Count += 1; }
                else Inventory.Cursor = item.Copy(l ? item.MaxStack : 1);
                return;
            }
            if (hover.Valid)
            {
                ClickSlot(hover, l, shift);
                Persist.Dirty = true;
                return;
            }
            if (!inPanel && Inventory.Cursor != null)
            {
                // Clicking outside the window throws the carried stack (right click: just one).
                var c = Inventory.Cursor;
                var one = r ? c.Copy(1) : c;
                if (r) { c.Count--; if (c.Count <= 0) Inventory.Cursor = null; } else Inventory.Cursor = null;
                Interaction.Throw(one);
            }
        }

        private static void ClickSlot(Slot slot, bool leftButton, bool shift)
        {
            var item = slot.Get;
            var cur = Inventory.Cursor;
            if (shift && item != null)
            {
                // Move to the other part of the screen: container <-> inventory, or hotbar <-> main inventory.
                Slot[] targets = TargetsFor(slot);
                slot.Set(null);
                for (int pass = 0; pass < 2 && item.Count > 0; pass++)
                    foreach (var t in targets)
                    {
                        if (item.Count <= 0) break;
                        if (!Allowed(t, item)) continue;
                        var ex = t.Get;
                        if (pass == 0 && ex != null && ex.SameItem(item) && ex.Count < ex.MaxStack)
                        {
                            int n = Mathf.Min(item.Count, ex.MaxStack - ex.Count); ex.Count += n; item.Count -= n;
                        }
                        else if (pass == 1 && ex == null) { t.Set(item.Copy(item.Count)); item.Count = 0; }
                    }
                if (item.Count > 0) slot.Set(item);
                return;
            }
            if (!Allowed(slot, cur)) return;
            if (leftButton)
            {
                if (cur == null) { Inventory.Cursor = item; slot.Set(null); }
                else if (item == null) { slot.Set(cur); Inventory.Cursor = null; }
                else if (item.SameItem(cur))
                {
                    int n = Mathf.Min(cur.Count, item.MaxStack - item.Count);
                    item.Count += n; cur.Count -= n;
                    if (cur.Count <= 0) Inventory.Cursor = null;
                }
                else { slot.Set(cur); Inventory.Cursor = item; }
            }
            else
            {
                if (cur == null && item != null)
                {
                    int half = (item.Count + 1) / 2;
                    Inventory.Cursor = item.Copy(half);
                    item.Count -= half;
                    if (item.Count <= 0) slot.Set(null);
                }
                else if (cur != null && (item == null || (item.SameItem(cur) && item.Count < item.MaxStack)))
                {
                    if (item == null) slot.Set(cur.Copy(1)); else item.Count++;
                    cur.Count--;
                    if (cur.Count <= 0) Inventory.Cursor = null;
                }
                else if (cur != null) { slot.Set(cur); Inventory.Cursor = item; }
            }
        }

        private static Slot[] TargetsFor(Slot from)
        {
            var l = new List<Slot>();
            if (StorageTab)
            {
                // Shift-click sends slimes between the hotbar and the storage pen.
                if (from.Arr == Inventory.SlimeStorage) for (int i = 0; i < 36; i++) l.Add(new Slot { Arr = Inventory.Slots, Index = i });
                else for (int i = 0; i < Inventory.SlimeStorage.Length; i++) l.Add(new Slot { Arr = Inventory.SlimeStorage, Index = i });
                return l.ToArray();
            }
            if (container != null)
            {
                if (from.Arr == container.Contents)
                {
                    for (int i = 8; i >= 0; i--) l.Add(new Slot { Arr = Inventory.Slots, Index = i });
                    for (int i = 35; i >= 9; i--) l.Add(new Slot { Arr = Inventory.Slots, Index = i });
                }
                else for (int i = 0; i < 27; i++) l.Add(new Slot { Arr = container.Contents, Index = i });
                return l.ToArray();
            }
            if (from.Index < 9) for (int i = 9; i < 36; i++) l.Add(new Slot { Arr = Inventory.Slots, Index = i });
            else for (int i = 0; i < 9; i++) l.Add(new Slot { Arr = Inventory.Slots, Index = i });
            return l.ToArray();
        }

        private static Rect TabRect(Tab t)
        {
            // Top tabs side by side from the left (26 px wide, 1 px apart); the inventory tab sits bottom right.
            return t.Bottom ? new Rect(left + W - 26, top + H - 4, 26, 32) : new Rect(left + t.Column * 27, top - 28, 26, 32);
        }

        // ------------------------------------------------------------------------------------------------ drawing

        public static void Draw()
        {
            if (!Open) return;
            Layout();
            var m = Mouse;
            McGui.Fill(0, 0, McGui.ScaledWidth, McGui.ScaledHeight, new Color(0.0627f, 0.0627f, 0.0627f, 0.78f));
            ItemStack tooltip = null;
            string tooltipText = null;
            var dark = new Color(0.25f, 0.25f, 0.25f);

            if (container != null)
            {
                if (container.Def.Shape == BlockShape.Shulker)
                    McGui.Blit(McAssets.Tex("gui/container/shulker_box.png"), left, top, 0, 0, W, H);
                else
                {
                    var g = McAssets.Tex("gui/container/generic_54.png");
                    McGui.Blit(g, left, top, 0, 0, W, 3 * 18 + 17);
                    McGui.Blit(g, left, top + 3 * 18 + 17, 0, 126, W, 96);
                }
                McFont.Draw(container.Def.Name, left + 8, top + 6, dark, false);
                McFont.Draw(McAssets.T("container.inventory", "Inventory"), left + 8, top + H - 94, dark, false);
            }
            else
            {
                var tabsTex = McAssets.Tex("gui/container/creative_inventory/tabs.png");
                var bg = McAssets.Tex(InventoryTab ? "gui/container/creative_inventory/tab_inventory.png" : "gui/container/creative_inventory/tab_items.png");
                for (int i = 0; i < tabs.Count; i++) if (i != tab) DrawTab(tabsTex, tabs[i], false);
                McGui.Blit(bg, left, top, 0, 0, W, H);
                DrawTab(tabsTex, tabs[tab], true);
                if (!InventoryTab) McFont.Draw(tabs[tab].Title, left + 8, top + 6, dark, false);

                if (Palette != null)
                {
                    bool canScroll = PaletteRows > 5;
                    McGui.Blit(tabsTex, left + 175, top + 18 + (int)((112 - 17) * scroll), canScroll ? 232 : 244, 0, 12, 15);
                    for (int row = 0; row < 5; row++)
                        for (int col = 0; col < 9; col++)
                        {
                            int idx = (ScrollRow + row) * 9 + col;
                            if (idx >= Palette.Count) continue;
                            int x = left + 9 + col * 18, y = top + 18 + row * 18;
                            Hud.DrawItem(Palette[idx], x, y);
                            if (idx == paletteHover) { Highlight(x, y); tooltip = Palette[idx]; }
                        }
                }
                else if (StorageTab)
                    McGui.Blit(tabsTex, left + 175, top + 18, 244, 0, 12, 15);
                else
                {
                    if (destroyHover) { Highlight(left + 173, top + 112); tooltipText = "Détruire l'objet"; }
                    DrawPreview(m);
                }
            }

            foreach (var kv in slotRects)
            {
                var r = kv.Key;
                Hud.DrawItem(kv.Value.Get, r.x, r.y);
                if (Grow(r).Contains(m)) { Highlight((int)r.x, (int)r.y); if (kv.Value.Get != null) tooltip = kv.Value.Get; }
            }

            if (Inventory.Cursor != null) Hud.DrawItem(Inventory.Cursor, m.x - 8, m.y - 8);
            else if (tooltip != null) Tooltip(TooltipText(tooltip), m);
            else if (tooltipText != null) Tooltip(tooltipText, m);
            else if (container == null) foreach (var t in tabs) if (TabRect(t).Contains(m)) Tooltip(t.Title, m);
        }

        private static string TooltipText(ItemStack s)
        {
            if (!s.HasContents) return s.Name;
            int n = 0;
            foreach (var c in s.Contents) if (c != null) n++;
            return s.Name + " (" + n + ")";
        }

        private static void DrawTab(Texture2D tabsTex, Tab t, bool selected)
        {
            var r = TabRect(t);
            int u = t.Column * 26;
            int v = t.Bottom ? (selected ? 96 : 64) : (selected ? 32 : 0);
            McGui.Blit(tabsTex, r.x, r.y, u, v, 26, 32);
            Hud.DrawItem(t.Icon, r.x + 5, r.y + (t.Bottom ? 7 : 9));
        }

        private static void Highlight(int x, int y) => McGui.Fill(x, y, 16, 16, new Color(1, 1, 1, 0.5f));

        private static void Tooltip(string text, Vector2 m)
        {
            float w = McFont.Width(text);
            float x = m.x + 12, y = m.y - 12;
            if (x + w + 4 > McGui.ScaledWidth) x = m.x - 16 - w;
            var bg = new Color(0.0627f, 0, 0.0627f, 0.94f);
            McGui.Fill(x - 3, y - 4, w + 6, 1, bg);
            McGui.Fill(x - 3, y + 8 + 3, w + 6, 1, bg);
            McGui.Fill(x - 3, y - 3, w + 6, 8 + 6, bg);
            McGui.Fill(x - 4, y - 3, 1, 8 + 6, bg);
            McGui.Fill(x + w + 3, y - 3, 1, 8 + 6, bg);
            var b1 = new Color(0.314f, 0f, 1f, 0.31f);
            var b2 = new Color(0.157f, 0f, 0.5f, 0.31f);
            McGui.Fill(x - 3, y - 3 + 1, 1, 8 + 6 - 2, b1);
            McGui.Fill(x + w + 2, y - 3 + 1, 1, 8 + 6 - 2, b1);
            McGui.Fill(x - 3, y - 3, w + 6, 1, b1);
            McGui.Fill(x - 3, y + 8 + 2, w + 6, 1, b2);
            McFont.Draw(text, x, y, Color.white);
        }

        /// <summary>Steve in the little black window, looking at the mouse like Minecraft's InventoryScreen.drawEntity.</summary>
        private static void DrawPreview(Vector2 m)
        {
            EnsurePreview();
            int s = McGui.Scale;
            int pw = 32 * s, ph = 43 * s;
            if (rt == null || rt.width != pw || rt.height != ph)
            {
                if (rt != null) rt.Release();
                rt = new RenderTexture(pw, ph, 16) { filterMode = FilterMode.Point };
                previewCam.targetTexture = rt;
            }
            previewRig.SetActive(true);
            float cx = left + 88, cy = top + 45 - 30;
            float ax = Mathf.Atan((cx - m.x) / 40f), ay = Mathf.Atan((cy - m.y) / 40f);
            previewSteve.transform.localRotation = Quaternion.Euler(0, 180f - ax * 20f, 0);
            previewSteve.Head.localRotation = Quaternion.Euler(-ay * 20f, -ax * 20f, 0);
            previewSteve.Body.localRotation = Quaternion.identity;
            previewCam.Render();
            GUI.DrawTexture(new Rect((left + 73) * s, (top + 6) * s, pw, ph), rt, ScaleMode.StretchToFill, true);
        }

        private static void EnsurePreview()
        {
            if (previewRig != null) return;
            previewRig = new GameObject("SlimeCraft Preview");
            previewRig.transform.position = new Vector3(0, -4000, 0);
            previewSteve = SteveBody.Create();
            previewSteve.transform.SetParent(previewRig.transform, false);
            previewSteve.SetVisible(true);
            ActorVisuals.SetLayer(previewSteve.gameObject, 30);
            var camGo = new GameObject("cam");
            camGo.transform.SetParent(previewRig.transform, false);
            camGo.transform.localPosition = new Vector3(0, 0.875f + 0.2f, -5f);
            previewCam = camGo.AddComponent<Camera>();
            previewCam.enabled = false;
            previewCam.orthographic = true;
            previewCam.orthographicSize = 43f / 20f / 2f;
            previewCam.cullingMask = 1 << 30;
            previewCam.clearFlags = CameraClearFlags.SolidColor;
            previewCam.backgroundColor = new Color(0, 0, 0, 0);
            previewCam.nearClipPlane = 0.1f; previewCam.farClipPlane = 20f;
            var lightGo = new GameObject("light");
            lightGo.transform.SetParent(previewRig.transform, false);
            lightGo.transform.rotation = Quaternion.Euler(30, 20, 0);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.cullingMask = 1 << 30;
            light.intensity = 1.1f;
            Object.DontDestroyOnLoad(previewRig);
        }

        public static void TestTab(int t) { EnsureTabs(); tab = Mathf.Clamp(t, 0, tabs.Count - 1); }

        public static void Destroy()
        {
            if (previewRig != null) Object.Destroy(previewRig);
            previewRig = null;
            if (rt != null) rt.Release();
            rt = null;
            Open = false;
            container = null;
            tabs = null;
        }
    }
}
