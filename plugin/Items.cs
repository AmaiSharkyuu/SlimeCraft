using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// One inventory stack, either a Minecraft item (with damage, enchantments, potion, colour, contents like
    /// Minecraft's NBT) or a Subnautica item (a TechType: titanium, a knife, a peeper...). Both games' things live
    /// side by side in the same hotbar, chests and crafting grids.
    /// </summary>
    public class ItemStack
    {
        public ItemDef Item;
        public TechType Tech = TechType.None;
        public int Count = 1;
        public int Damage;
        public Dictionary<string, int> Ench;          // enchantments (also the stored ones of an enchanted book)
        public string Potion;                         // potion id for potions, splash/lingering potions and tipped arrows
        public string CustomName;
        public int Color = -1;                        // dyed leather, firework star
        public int Flight;                            // firework rocket flight duration
        public int RepairCost;
        public ItemStack[] Contents;                  // shulker box contents

        public ItemStack(ItemDef d, int n = 1) { Item = d; Count = n; }
        public ItemStack(TechType t, int n = 1) { Tech = t; Count = n; }

        public static ItemStack Of(string id, int n = 1)
        {
            var d = McData.Item(id);
            return d != null ? new ItemStack(d, n) : null;
        }

        public bool IsMc => Item != null;
        public bool IsSn => Tech != TechType.None;
        public string Id => IsMc ? Item.Id : "sn:" + Tech;
        public bool Is(string id) => Item != null && Item.Id == id;
        public bool HasTag(string tag) => Item != null && Item.Tags.Contains(tag);

        public int MaxStack => IsMc ? Item.Stack : SnItems.MaxStack(Tech);
        public bool Damageable => IsMc && Item.Durability > 0;
        public bool Enchanted => Ench != null && Ench.Count > 0;
        public int Level(string ench) => Ench != null && Ench.TryGetValue(ench, out var l) ? l : 0;

        public ItemStack Copy(int n = -1)
        {
            var c = (ItemStack)MemberwiseClone();
            if (n >= 0) c.Count = n;
            if (Ench != null) c.Ench = new Dictionary<string, int>(Ench);
            if (Contents != null) c.Contents = (ItemStack[])Contents.Clone();
            return c;
        }

        public bool SameItem(ItemStack o)
        {
            if (o == null || o.Item != Item || o.Tech != Tech || o.Damage != Damage || o.Potion != Potion || o.CustomName != CustomName || o.Color != Color || o.Flight != Flight) return false;
            if (Contents != null || o.Contents != null) return false;
            int a = Ench?.Count ?? 0, b = o.Ench?.Count ?? 0;
            if (a != b) return false;
            if (a > 0) foreach (var kv in Ench) if (!o.Ench.TryGetValue(kv.Key, out var l) || l != kv.Value) return false;
            return true;
        }

        public string Name
        {
            get
            {
                if (CustomName != null) return CustomName;
                if (IsSn) return SnItems.Name(Tech);
                if (Potion != null && (Item.Id.EndsWith("potion") || Item.Id == "tipped_arrow"))
                {
                    var prefix = Item.Id == "potion" ? "item.minecraft.potion.effect." : Item.Id == "splash_potion" ? "item.minecraft.splash_potion.effect." : Item.Id == "lingering_potion" ? "item.minecraft.lingering_potion.effect." : "item.minecraft.tipped_arrow.effect.";
                    return McData.T(prefix + Potion, Item.Name);
                }
                return Item.Name;
            }
        }

        /// <summary>Minecraft's name colour by rarity (enchanted items go up one step).</summary>
        public Color NameColor
        {
            get
            {
                if (!IsMc) return UnityEngine.Color.white;
                var r = Item.Rarity;
                if (Enchanted) r = r == "common" || r == "uncommon" ? "rare" : "epic";
                switch (r)
                {
                    case "uncommon": return new Color(1f, 1f, 0.333f);
                    case "rare": return new Color(0.333f, 1f, 1f);
                    case "epic": return new Color(1f, 0.333f, 1f);
                    default: return UnityEngine.Color.white;
                }
            }
        }

        public JObject Save()
        {
            var o = new JObject { ["id"] = Id, ["n"] = Count };
            if (Damage != 0) o["dmg"] = Damage;
            if (Enchanted) o["ench"] = JObject.FromObject(Ench);
            if (Potion != null) o["potion"] = Potion;
            if (CustomName != null) o["name"] = CustomName;
            if (Color >= 0) o["color"] = Color;
            if (Flight != 0) o["flight"] = Flight;
            if (RepairCost != 0) o["repair"] = RepairCost;
            if (Contents != null) o["contents"] = SaveList(Contents);
            return o;
        }

        public static ItemStack Load(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            var id = (string)t["id"];
            ItemStack s;
            if (id.StartsWith("sn:"))
            {
                if (!TechTypeExtensions.FromString(id.Substring(3), out var tt, true)) return null;
                s = new ItemStack(tt, (int)t["n"]);
            }
            else
            {
                var d = McData.Item(id);
                if (d == null) return null;
                s = new ItemStack(d, (int)t["n"]);
            }
            if (t["dmg"] != null) s.Damage = (int)t["dmg"];
            if (t["ench"] != null) s.Ench = t["ench"].ToObject<Dictionary<string, int>>();
            if (t["potion"] != null) s.Potion = (string)t["potion"];
            if (t["name"] != null) s.CustomName = (string)t["name"];
            if (t["color"] != null) s.Color = (int)t["color"];
            if (t["flight"] != null) s.Flight = (int)t["flight"];
            if (t["repair"] != null) s.RepairCost = (int)t["repair"];
            if (t["contents"] != null) s.Contents = LoadList(t["contents"], 27);
            return s;
        }

        public static JArray SaveList(ItemStack[] list)
        {
            var a = new JArray();
            foreach (var s in list) a.Add(s == null ? (JToken)JValue.CreateNull() : s.Save());
            return a;
        }

        public static ItemStack[] LoadList(JToken t, int size)
        {
            var r = new ItemStack[size];
            if (t is JArray a) for (int i = 0; i < a.Count && i < size; i++) r[i] = Load(a[i]);
            return r;
        }

        public override string ToString() => Count + "x " + Id;
    }

    /// <summary>What SubnautiCraft needs to know about Subnautica's items.</summary>
    public static class SnItems
    {
        private static readonly Dictionary<TechType, int> stack = new Dictionary<TechType, int>();

        public static string Name(TechType t)
        {
            try { return Language.main.Get(t.AsString()); } catch { return t.ToString(); }
        }

        public static bool IsTool(TechType t)
        {
            try { return TechData.GetEquipmentType(t) == EquipmentType.Hand; } catch { return false; }
        }

        public static bool IsEquipment(TechType t)
        {
            try { var e = TechData.GetEquipmentType(t); return e != EquipmentType.None && e != EquipmentType.Hand; } catch { return false; }
        }

        /// <summary>Tools, equipment, batteries and anything with a charge stay one per slot; everything else stacks to 64.</summary>
        public static int MaxStack(TechType t)
        {
            if (stack.TryGetValue(t, out var n)) return n;
            n = 64;
            if (IsTool(t) || IsEquipment(t)) n = 1;
            var s = t.ToString();
            if (s.Contains("Battery") || s.Contains("PowerCell")) n = 1;
            stack[t] = n;
            return n;
        }

        public static Sprite Icon(TechType t)
        {
            try { return SpriteManager.Get(t); } catch { return null; }
        }
    }

    /// <summary>
    /// Minecraft's player inventory: 9 hotbar slots (0..8), 27 main slots (9..35), 4 armour slots (0 feet .. 3 head,
    /// like Minecraft's PlayerInventory.armor) and the offhand.
    /// </summary>
    public static class Inventory
    {
        public static readonly ItemStack[] Main = new ItemStack[36];
        public static readonly ItemStack[] Armor = new ItemStack[4];
        public static ItemStack Offhand;
        public static ItemStack Cursor;
        public static int Selected;
        public static readonly ItemStack[] EnderChest = new ItemStack[27];

        public static ItemStack Held { get => Main[Selected]; set => Main[Selected] = value; }

        public static ItemStack ArmorIn(string slot)
        {
            switch (slot) { case "feet": return Armor[0]; case "legs": return Armor[1]; case "chest": return Armor[2]; case "head": return Armor[3]; }
            return null;
        }

        public static int ArmorIndex(string slot) => slot == "feet" ? 0 : slot == "legs" ? 1 : slot == "chest" ? 2 : slot == "head" ? 3 : -1;

        public static void Clear()
        {
            for (int i = 0; i < Main.Length; i++) Main[i] = null;
            for (int i = 0; i < 4; i++) Armor[i] = null;
            for (int i = 0; i < EnderChest.Length; i++) EnderChest[i] = null;
            Offhand = null; Cursor = null; Selected = 0;
        }

        /// <summary>Minecraft's insertStack: top up matching stacks (selected slot, offhand, hotbar, main), then the first empty slot.</summary>
        public static int Add(ItemStack s)
        {
            if (s == null || s.Count <= 0) return 0;
            int left = s.Count;
            void Top(ref ItemStack t)
            {
                if (left <= 0 || t == null || !t.SameItem(s) || t.Count >= t.MaxStack) return;
                int m = Mathf.Min(left, t.MaxStack - t.Count);
                t.Count += m; left -= m;
            }
            Top(ref Main[Selected]);
            Top(ref Offhand);
            for (int i = 0; i < 36 && left > 0; i++) Top(ref Main[i]);
            for (int i = 0; i < 36 && left > 0; i++)
            {
                if (Main[i] != null) continue;
                int m = Mathf.Min(left, s.MaxStack);
                Main[i] = s.Copy(m); left -= m;
            }
            return s.Count - left;
        }

        public static bool CanFit(ItemStack s)
        {
            foreach (var t in Main)
                if (t == null || (t.SameItem(s) && t.Count < t.MaxStack)) return true;
            return false;
        }

        public static int CountOf(System.Func<ItemStack, bool> pred)
        {
            int n = 0;
            foreach (var t in Main) if (t != null && pred(t)) n += t.Count;
            if (Offhand != null && pred(Offhand)) n += Offhand.Count;
            return n;
        }

        /// <summary>Removes n items matching pred, starting from the end of the inventory, returning how many went.</summary>
        public static int Remove(System.Func<ItemStack, bool> pred, int n, bool fromSelectedFirst = false)
        {
            int left = n;
            void Take(ref ItemStack t)
            {
                if (left <= 0 || t == null || !pred(t)) return;
                int m = Mathf.Min(left, t.Count);
                t.Count -= m; left -= m;
                if (t.Count <= 0) t = null;
            }
            if (fromSelectedFirst) Take(ref Main[Selected]);
            if (Cursor != null) Take(ref Cursor);
            for (int i = 35; i >= 9 && left > 0; i--) Take(ref Main[i]);
            Take(ref Offhand);
            for (int i = 8; i >= 0 && left > 0; i--) Take(ref Main[i]);
            return n - left;
        }

        public static void ShrinkHeld(int n = 1)
        {
            var h = Held;
            if (h == null || GameMode.Creative) return;
            if (h.IsSn) { SnBridge.Consume(h.Tech, n); }
            h.Count -= n;
            if (h.Count <= 0) Held = null;
        }

        /// <summary>Minecraft's item damage with Unbreaking's chance to skip; breaks the item (with sound) when worn out.</summary>
        public static void DamageHeld(int amount) => DamageStack(ref Main[Selected], amount);

        public static void DamageStack(ref ItemStack s, int amount)
        {
            if (s == null || !s.Damageable || GameMode.Creative) return;
            int unbreaking = s.Level("unbreaking");
            for (int i = 0; i < amount; i++)
            {
                if (unbreaking > 0)
                {
                    // UnbreakingEnchantment.shouldPreventDamage
                    bool armor = s.Item.ArmorSlot != null;
                    bool prevent = !(armor && Random.value < 0.6f) && Random.Range(0, unbreaking + 1) > 0;
                    if (prevent) continue;
                }
                s.Damage++;
            }
            if (s.Damage >= s.Item.Durability)
            {
                Sounds.Play("entity.item.break", Player.main != null ? Player.main.transform.position : (Vector3?)null);
                Particles.ItemBreak(s);
                s = null;
            }
        }

        public static JObject Save()
        {
            var o = new JObject
            {
                ["sel"] = Selected,
                ["main"] = ItemStack.SaveList(Main),
                ["armor"] = ItemStack.SaveList(Armor),
                ["offhand"] = Offhand?.Save(),
                ["ender"] = ItemStack.SaveList(EnderChest),
            };
            return o;
        }

        public static void Load(JObject o)
        {
            Clear();
            if (o == null) return;
            Selected = Mathf.Clamp((int?)o["sel"] ?? 0, 0, 8);
            var m = ItemStack.LoadList(o["main"], 36); for (int i = 0; i < 36; i++) Main[i] = m[i];
            var a = ItemStack.LoadList(o["armor"], 4); for (int i = 0; i < 4; i++) Armor[i] = a[i];
            Offhand = ItemStack.Load(o["offhand"]);
            var e = ItemStack.LoadList(o["ender"], 27); for (int i = 0; i < 27; i++) EnderChest[i] = e[i];
        }
    }
}
