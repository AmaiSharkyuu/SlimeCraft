using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Keeps Subnautica's own inventory and the Minecraft inventory one and the same.
    ///
    /// Two kinds of Subnautica items:
    ///  * "virtual" ones (titanium, quartz, fish, food, water...) live only as counts in Minecraft slots. When one
    ///    lands in Subnautica's container by any route (picking up, fabricator output, console), it is taken back out,
    ///    its object destroyed and the count added to the Minecraft inventory. Subnautica's questions about the
    ///    inventory (how many titanium do I have? remove two for this recipe; is there room?) are answered from the
    ///    Minecraft slots by the patches below, so fabricators, the habitat builder and the PDA recipes just work.
    ///  * "real" ones (tools, equipment, batteries) keep their game object, charge and state in Subnautica's
    ///    container; the Minecraft slots only mirror how many there are, and holding one in the hotbar equips it.
    /// </summary>
    public static class SnBridge
    {
        private static readonly Dictionary<TechType, int> pendingReal = new Dictionary<TechType, int>();
        private static readonly List<TechType> types = new List<TechType>();
        private static readonly Dictionary<TechType, int> mcCount = new Dictionary<TechType, int>();
        public static bool Suspend;
        private static InventoryItem equipped;
        private static int equippedSlot = -1;

        public static bool IsVirtual(TechType t) => t != TechType.None && SnItems.MaxStack(t) > 1;

        private static ItemsContainer Container => global::Inventory.main != null ? global::Inventory.main.container : null;

        public static int VirtualCount(TechType t)
        {
            if (!Game.Active || !IsVirtual(t)) return 0;
            return Inventory.CountOf(s => s.Tech == t || StandsIn(s, t)) + CursorCount(t) + Screens.HeldGridCount(t);
        }

        /// <summary>A Minecraft item a fabricator accepts in place of a Subnautica ingredient (an iron ingot for titanium...).</summary>
        public static bool StandsIn(ItemStack s, TechType t) => s.IsMc && Crafting.McToSn.TryGetValue(s.Item.Id, out var sn) && sn == t;

        private static int CursorCount(TechType t) => Inventory.Cursor != null && Inventory.Cursor.Tech == t ? Inventory.Cursor.Count : 0;

        /// <summary>Removes n Subnautica items the player owns (virtual counts or real objects), e.g. after eating one.</summary>
        public static void Consume(TechType t, int n)
        {
            if (IsVirtual(t)) return; // the caller shrinks the Minecraft stack itself
            for (int i = 0; i < n; i++)
            {
                var p = Container?.RemoveItem(t);
                if (p != null) Object.Destroy(p.gameObject);
            }
        }

        /// <summary>Takes n real items out of Subnautica's container (to drop, or to store in a Minecraft chest).</summary>
        public static void TakeReal(TechType t, int n, bool destroy)
        {
            if (IsVirtual(t)) return;
            for (int i = 0; i < n; i++)
            {
                var p = Container?.RemoveItem(t);
                if (p == null) break;
                if (destroy) Object.Destroy(p.gameObject);
                else
                {
                    var cam = MainCamera.camera.transform;
                    p.Drop(cam.position + cam.forward * 0.7f, cam.forward * 3f);
                }
            }
        }

        /// <summary>Spawns n real items back into Subnautica's container (taken out of a Minecraft chest, creative inventory...).</summary>
        public static void GiveReal(TechType t, int n)
        {
            if (IsVirtual(t) || n <= 0) return;
            pendingReal.TryGetValue(t, out var p);
            pendingReal[t] = p + n;
            UWE.CoroutineHost.StartCoroutine(Spawn(t, n));
        }

        private static System.Collections.IEnumerator Spawn(TechType t, int n)
        {
            for (int i = 0; i < n; i++)
            {
                var res = new TaskResult<GameObject>();
                yield return CraftData.AddToInventoryAsync(t, res, 1, true, true);
                pendingReal[t] = Mathf.Max(0, pendingReal[t] - 1);
            }
        }

        /// <summary>Every frame: convert virtual items that entered the container, reconcile real counts, equip the held tool.</summary>
        public static void Update()
        {
            var c = Container;
            if (c == null || Suspend) return;

            // 1. virtual items that slipped into the container (pickups, fabricator output, console) become counts
            types.Clear();
            types.AddRange(c.GetItemTypes());
            foreach (var t in types)
            {
                if (!IsVirtual(t)) continue;
                var items = new List<InventoryItem>(c.GetItems(t));
                foreach (var it in items)
                {
                    if (it.item == null) continue;
                    c.RemoveItem(it.item, true);
                    Object.Destroy(it.item.gameObject);
                    int took = Inventory.Add(new ItemStack(t, 1));
                    if (took == 0) ItemEntity.Spawn(new ItemStack(t, 1), Player.main.transform.position, Vector3.zero, 1.5f);
                }
            }

            // 2. real items: the container is the truth, the Minecraft slots follow
            mcCount.Clear();
            void Count(ItemStack s) { if (s != null && s.IsSn && !IsVirtual(s.Tech)) { mcCount.TryGetValue(s.Tech, out var k); mcCount[s.Tech] = k + s.Count; } }
            foreach (var s in Inventory.Main) Count(s);
            Count(Inventory.Offhand);
            Count(Inventory.Cursor);
            Screens.ForEachGridItem(Count);
            types.Clear();
            types.AddRange(c.GetItemTypes());
            foreach (var kv in mcCount) if (!types.Contains(kv.Key)) types.Add(kv.Key);
            foreach (var t in types)
            {
                if (IsVirtual(t)) continue;
                int sn = c.GetCount(t);
                pendingReal.TryGetValue(t, out var pend);
                mcCount.TryGetValue(t, out var mc);
                int diff = sn + pend - mc;
                if (diff > 0)
                {
                    int took = Inventory.Add(new ItemStack(t, diff));
                    if (took < diff) TakeReal(t, diff - took, false); // no room: it falls out, like a full Minecraft inventory
                }
                else if (diff < 0) Inventory.Remove(s => s.Tech == t, -diff);
            }

            UpdateHeldTool();
        }

        /// <summary>Holding a Subnautica tool in the hotbar puts the real tool in Subnautica's hand.</summary>
        private static void UpdateHeldTool()
        {
            var inv = global::Inventory.main;
            var qs = inv.quickSlots;
            if (qs == null) return;
            qs.SetIgnoreHotkeyInput(true);
            qs.SetIgnoreScrollInput(true);
            var held = Inventory.Held;
            bool wantTool = held != null && held.IsSn && SnItems.IsTool(held.Tech) && Player.main.GetMode() == Player.Mode.Normal && !Motor.SuspendedByGame;
            var cur = qs.heldItem;
            if (!wantTool)
            {
                if (cur != null && Player.main.GetMode() == Player.Mode.Normal) qs.DeselectImmediate();
                equipped = null;
                return;
            }
            if (cur != null && cur.item != null && cur.item.GetTechType() == held.Tech) return;
            var items = inv.container.GetItems(held.Tech);
            if (items == null || items.Count == 0) return;
            var it = items[0];
            qs.Bind(0, it);
            qs.SelectImmediate(0);
            equipped = it;
        }

        public static PlayerTool HeldTool => global::Inventory.main != null ? global::Inventory.main.GetHeldTool() : null;
    }

    /// <summary>Subnautica's inventory questions answered from the Minecraft slots for virtual items.</summary>
    [HarmonyPatch]
    internal static class BridgePatches
    {
        private static bool IsMain(ItemsContainer c) => Game.Active && global::Inventory.main != null && ReferenceEquals(c, global::Inventory.main.container);

        [HarmonyPatch(typeof(global::Inventory), nameof(global::Inventory.GetPickupCount)), HarmonyPostfix]
        private static void PickupCount(TechType pickupType, ref int __result) => __result += SnBridge.VirtualCount(pickupType);

        [HarmonyPatch(typeof(global::Inventory), nameof(global::Inventory.DestroyItem)), HarmonyPrefix]
        private static bool Destroy(TechType destroyTechType, ref bool __result)
        {
            if (!Game.Active || !SnBridge.IsVirtual(destroyTechType)) return true;
            if (Inventory.Remove(s => s.Tech == destroyTechType, 1) > 0 || Screens.TakeFromGrids(destroyTechType)
                || Inventory.Remove(s => SnBridge.StandsIn(s, destroyTechType), 1) > 0) { __result = true; return false; }
            return true;
        }

        /// <summary>Picking up a virtual item goes straight into the Minecraft slots, whatever room the 6x8 grid has left.</summary>
        [HarmonyPatch(typeof(global::Inventory), nameof(global::Inventory.Pickup)), HarmonyPrefix]
        private static bool Pickup(Pickupable pickupable, bool noMessage, ref bool __result)
        {
            if (!Game.Active || pickupable == null) return true;
            var t = pickupable.GetTechType();
            if (!SnBridge.IsVirtual(t)) return true;
            var stack = new ItemStack(t, 1);
            if (!Inventory.CanFit(stack)) { __result = false; return false; }
            pickupable.Pickup();
            Inventory.Add(stack);
            KnownTech.Analyze(t, verbose: false);
            if (!noMessage) uGUI_IconNotifier.main.Play(t, uGUI_IconNotifier.AnimationType.From);
            Sounds.Play("entity.item.pickup", pickupable.transform.position, 0.2f, ((Random.value - Random.value) * 0.7f + 1f) * 2f);
            Object.Destroy(pickupable.gameObject);
            __result = true;
            return false;
        }

        [HarmonyPatch(typeof(ItemsContainer), nameof(ItemsContainer.GetCount)), HarmonyPostfix]
        private static void Count(ItemsContainer __instance, TechType techType, ref int __result)
        {
            if (IsMain(__instance)) __result += SnBridge.VirtualCount(techType);
        }

        [HarmonyPatch(typeof(ItemsContainer), nameof(ItemsContainer.Contains), typeof(TechType)), HarmonyPostfix]
        private static void Contains(ItemsContainer __instance, TechType techType, ref bool __result)
        {
            if (!__result && IsMain(__instance)) __result = SnBridge.VirtualCount(techType) > 0;
        }

        [HarmonyPatch(typeof(ItemsContainer), nameof(ItemsContainer.HasRoomFor), typeof(Pickupable)), HarmonyPostfix]
        private static void Room(ItemsContainer __instance, Pickupable pickupable, ref bool __result)
        {
            if (__result || !IsMain(__instance) || pickupable == null) return;
            var t = pickupable.GetTechType();
            if (SnBridge.IsVirtual(t)) __result = Inventory.CanFit(new ItemStack(t, 1));
        }

        [HarmonyPatch(typeof(ItemsContainer), nameof(ItemsContainer.HasRoomFor), typeof(TechType)), HarmonyPostfix]
        private static void RoomT(ItemsContainer __instance, TechType techType, ref bool __result)
        {
            if (__result || !IsMain(__instance)) return;
            if (SnBridge.IsVirtual(techType)) __result = Inventory.CanFit(new ItemStack(techType, 1));
        }
    }
}
