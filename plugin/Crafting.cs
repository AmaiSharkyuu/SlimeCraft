using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Minecraft's recipes (every one in the jar, read from the dump) matched like Minecraft's RecipeManager:
    /// shaped (any position in the grid, mirrored too), shapeless, the special ones (firework rockets, repairing,
    /// dyeing leather, tipped arrows, shulker box colouring), furnace / blast furnace / smoker / campfire cooking,
    /// the stonecutter and the smithing table.
    ///
    /// Slime Rancher's things are Minecraft ingredients too: plorts stand for the Minecraft material they look like
    /// (a boom plort is gunpowder, a fire plort blaze powder, a quantum plort an ender pearl, a gold plort a gold
    /// ingot...), fruits and veggies for Minecraft's crops, and hens for raw chicken (cooked in a furnace).
    /// </summary>
    public static class Crafting
    {
        public static readonly Dictionary<Identifiable.Id, string> SrToMc = new Dictionary<Identifiable.Id, string>
        {
            // plorts
            { Identifiable.Id.PINK_PLORT, "pink_dye" }, { Identifiable.Id.ROCK_PLORT, "flint" }, { Identifiable.Id.TABBY_PLORT, "string" },
            { Identifiable.Id.PHOSPHOR_PLORT, "glowstone_dust" }, { Identifiable.Id.RAD_PLORT, "redstone" }, { Identifiable.Id.BOOM_PLORT, "gunpowder" },
            { Identifiable.Id.HONEY_PLORT, "honeycomb" }, { Identifiable.Id.HUNTER_PLORT, "leather" }, { Identifiable.Id.CRYSTAL_PLORT, "amethyst_shard" },
            { Identifiable.Id.QUANTUM_PLORT, "ender_pearl" }, { Identifiable.Id.DERVISH_PLORT, "feather" }, { Identifiable.Id.MOSAIC_PLORT, "prismarine_crystals" },
            { Identifiable.Id.TANGLE_PLORT, "slime_ball" }, { Identifiable.Id.FIRE_PLORT, "blaze_powder" }, { Identifiable.Id.SABER_PLORT, "bone" },
            { Identifiable.Id.PUDDLE_PLORT, "prismarine_shard" }, { Identifiable.Id.GOLD_PLORT, "gold_ingot" }, { Identifiable.Id.QUICKSILVER_PLORT, "iron_ingot" },
            // fruits and veggies
            { Identifiable.Id.POGO_FRUIT, "apple" }, { Identifiable.Id.CUBERRY_FRUIT, "sweet_berries" }, { Identifiable.Id.MANGO_FRUIT, "apple" },
            { Identifiable.Id.LEMON_FRUIT, "apple" }, { Identifiable.Id.PEAR_FRUIT, "apple" }, { Identifiable.Id.KOOKADOBA_FRUIT, "glow_berries" },
            { Identifiable.Id.CARROT_VEGGIE, "carrot" }, { Identifiable.Id.BEET_VEGGIE, "beetroot" }, { Identifiable.Id.PARSNIP_VEGGIE, "potato" },
            { Identifiable.Id.OCAOCA_VEGGIE, "potato" }, { Identifiable.Id.ONION_VEGGIE, "beetroot" }, { Identifiable.Id.GINGER_VEGGIE, "carrot" },
            // meat
            { Identifiable.Id.HEN, "chicken" }, { Identifiable.Id.ROOSTER, "chicken" }, { Identifiable.Id.BRIAR_HEN, "chicken" },
            { Identifiable.Id.STONY_HEN, "chicken" }, { Identifiable.Id.PAINTED_HEN, "chicken" }, { Identifiable.Id.ELDER_HEN, "chicken" },
            { Identifiable.Id.ELDER_ROOSTER, "chicken" }, { Identifiable.Id.CHICK, "egg" },
        };

        /// <summary>Nothing goes the other way: Slime Rancher has no crafting of its own that would take Minecraft items.</summary>
        public static readonly Dictionary<string, Identifiable.Id> McToSn = new Dictionary<string, Identifiable.Id>();

        /// <summary>Slime Rancher things do not cook into Slime Rancher things; a hen in a furnace gives Minecraft's cooked chicken.</summary>
        private static Identifiable.Id CookSn(Identifiable.Id t) => Identifiable.Id.NONE;

        public static bool Matches(ItemStack s, string[] options)
        {
            if (options == null || options.Length == 0) return s == null;
            if (s == null) return false;
            string id = s.IsMc ? s.Item.Id : SrToMc.TryGetValue(s.Actor, out var m) ? m : null;
            if (id == null) return false;
            foreach (var o in options) if (o == id) return true;
            return false;
        }

        public class Result
        {
            public ItemStack Output;
            public Recipe Recipe;
            public bool Special;
        }

        // ------------------------------------------------------------------------------------------------ crafting grid

        /// <summary>The recipe a crafting grid (w x h, row-major) makes, if any.</summary>
        public static Result Craft(ItemStack[] grid, int w, int h)
        {
            int minX = w, minY = h, maxX = -1, maxY = -1, filled = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (grid[y * w + x] != null)
                    {
                        filled++;
                        minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x);
                        minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
                    }
            if (filled == 0) return null;
            // a lone Subnautica item turns into its Minecraft counterpart (before recipes, so titanium makes an
            // iron ingot rather than iron nuggets)
            if (filled == 1)
                foreach (var s0 in grid)
                    if (s0 != null && s0.IsSr && SrToMc.TryGetValue(s0.Actor, out var mc0) && McData.Item(mc0) != null)
                        return new Result { Output = ItemStack.Of(mc0), Special = true };
            int bw = maxX - minX + 1, bh = maxY - minY + 1;

            foreach (var r in McData.Recipes)
            {
                if (r.Type != "crafting" || r.Result == null) continue;
                if (r.Serializer == "crafting_shaped")
                {
                    if (r.Width != bw || r.Height != bh) continue;
                    if (ShapedFits(r, grid, w, minX, minY, false) || ShapedFits(r, grid, w, minX, minY, true))
                        return new Result { Output = Output(r), Recipe = r };
                }
                else if (r.Serializer == "crafting_shapeless")
                {
                    if (r.Ingredients.Count != filled) continue;
                    if (ShapelessFits(r, grid)) return new Result { Output = Output(r), Recipe = r };
                }
            }
            var special = SpecialCraft(grid, filled);
            if (special != null) return new Result { Output = special, Special = true };
            return null;
        }

        private static ItemStack Output(Recipe r)
        {
            var s = ItemStack.Of(r.Result, r.Count);
            return s;
        }

        private static bool ShapedFits(Recipe r, ItemStack[] grid, int w, int ox, int oy, bool mirror)
        {
            for (int y = 0; y < r.Height; y++)
                for (int x = 0; x < r.Width; x++)
                {
                    int rx = mirror ? r.Width - 1 - x : x;
                    var ing = r.Ingredients[y * r.Width + rx];
                    var s = grid[(oy + y) * w + ox + x];
                    if (ing.Length == 0) { if (s != null) return false; continue; }
                    if (!Matches(s, ing)) return false;
                }
            return true;
        }

        private static bool ShapelessFits(Recipe r, ItemStack[] grid)
        {
            var used = new bool[grid.Length];
            foreach (var ing in r.Ingredients)
            {
                bool found = false;
                for (int i = 0; i < grid.Length; i++)
                {
                    if (used[i] || grid[i] == null) continue;
                    if (Matches(grid[i], ing)) { used[i] = true; found = true; break; }
                }
                if (!found) return false;
            }
            return true;
        }

        /// <summary>Takes one of every ingredient (leaving buckets and bottles behind like Minecraft's recipe remainders).</summary>
        public static void Consume(ItemStack[] grid)
        {
            for (int i = 0; i < grid.Length; i++)
            {
                var s = grid[i];
                if (s == null) continue;
                ItemStack remainder = null;
                if (s.IsMc)
                {
                    switch (s.Item.Id)
                    {
                        case "milk_bucket": case "water_bucket": case "lava_bucket": case "powder_snow_bucket": remainder = ItemStack.Of("bucket"); break;
                        case "honey_bottle": case "dragon_breath": remainder = ItemStack.Of("glass_bottle"); break;
                    }
                }
                s.Count--;
                grid[i] = s.Count <= 0 ? remainder : s;
                if (s.Count > 0 && remainder != null) Inventory.Add(remainder);
            }
        }

        private static ItemStack SpecialCraft(ItemStack[] grid, int filled)
        {
            var items = new List<ItemStack>();
            foreach (var s in grid) if (s != null) items.Add(s);

            // firework rocket: paper + 1..3 gunpowder (+ stars)
            int paper = 0, gunpowder = 0, other = 0;
            foreach (var s in items)
            {
                if (s.Is("paper")) paper++;
                else if (Matches(s, new[] { "gunpowder" })) gunpowder++;
                else if (!s.Is("firework_star")) other++;
            }
            if (paper == 1 && gunpowder >= 1 && gunpowder <= 3 && other == 0) return new ItemStack(McData.Item("firework_rocket"), 3) { Flight = gunpowder };

            // repair: two of the same damageable item
            if (filled == 2 && items[0].Damageable && items[1].IsMc && items[0].Item == items[1].Item && items[0].Item.Stack == 1)
            {
                int max = items[0].Item.Durability;
                int left = (max - items[0].Damage) + (max - items[1].Damage) + max * 5 / 100;
                var r = new ItemStack(items[0].Item) { Damage = Mathf.Max(0, max - left) };
                return r;
            }

            // dyeing leather armour
            ItemStack armor = null; var dyes = new List<string>(); bool bad = false;
            foreach (var s in items)
            {
                if (s.IsMc && s.Item.Id.StartsWith("leather_") && s.Item.ArmorSlot != null) { if (armor != null) bad = true; armor = s; }
                else if (s.IsMc && s.Item.Id.EndsWith("_dye")) dyes.Add(s.Item.Id.Replace("_dye", ""));
                else bad = true;
            }
            if (!bad && armor != null && dyes.Count > 0)
            {
                var r = armor.Copy(1);
                r.Color = MixDyes(armor.Color, dyes);
                return r;
            }

            // tipped arrows: 8 arrows around a lingering potion
            if (filled == 9 && grid.Length == 9 && grid[4] != null && grid[4].Is("lingering_potion"))
            {
                bool arrows = true;
                for (int i = 0; i < 9; i++) if (i != 4 && (grid[i] == null || !grid[i].Is("arrow"))) arrows = false;
                if (arrows) return new ItemStack(McData.Item("tipped_arrow"), 8) { Potion = grid[4].Potion };
            }

            // shulker box colouring
            if (filled == 2)
            {
                ItemStack box = null, dye = null;
                foreach (var s in items) { if (s.IsMc && s.Item.Id.EndsWith("shulker_box")) box = s; else if (s.IsMc && s.Item.Id.EndsWith("_dye")) dye = s; }
                if (box != null && dye != null)
                {
                    var target = McData.Item(dye.Item.Id.Replace("_dye", "") + "_shulker_box");
                    if (target != null) { var r = box.Copy(1); r.Item = target; return r; }
                }
            }
            return null;
        }

        private static readonly Dictionary<string, int> dyeColors = new Dictionary<string, int>
        {
            {"white",0xF9FFFE},{"orange",0xF9801D},{"magenta",0xC74EBD},{"light_blue",0x3AB3DA},{"yellow",0xFED83D},{"lime",0x80C71F},
            {"pink",0xF38BAA},{"gray",0x474F52},{"light_gray",0x9D9D97},{"cyan",0x169C9C},{"purple",0x8932B8},{"blue",0x3C44AA},
            {"brown",0x835432},{"green",0x5E7C16},{"red",0xB02E26},{"black",0x1D1D21},
        };

        /// <summary>DyeableItem.blendAndSetColor.</summary>
        private static int MixDyes(int existing, List<string> dyes)
        {
            int r = 0, g = 0, b = 0, maxSum = 0, n = 0;
            if (existing >= 0)
            {
                int er = (existing >> 16) & 255, eg = (existing >> 8) & 255, eb = existing & 255;
                maxSum += Mathf.Max(er, Mathf.Max(eg, eb)); r += er; g += eg; b += eb; n++;
            }
            foreach (var d in dyes)
            {
                if (!dyeColors.TryGetValue(d, out var c)) continue;
                int cr = (c >> 16) & 255, cg = (c >> 8) & 255, cb = c & 255;
                maxSum += Mathf.Max(cr, Mathf.Max(cg, cb)); r += cr; g += cg; b += cb; n++;
            }
            if (n == 0) return existing;
            int ar = r / n, ag = g / n, ab = b / n;
            float avgMax = (float)maxSum / n;
            float m = Mathf.Max(ar, Mathf.Max(ag, ab));
            ar = (int)(ar * avgMax / m); ag = (int)(ag * avgMax / m); ab = (int)(ab * avgMax / m);
            return (ar << 16) | (ag << 8) | ab;
        }

        // ------------------------------------------------------------------------------------------------ cooking, cutting, smithing

        /// <summary>Cooking result for a furnace kind ("smelting", "blasting", "smoking", "campfire_cooking").</summary>
        public static ItemStack Smelt(string type, ItemStack input) => SmeltRecipe(type, input, out _);

        public static ItemStack SmeltRecipe(string type, ItemStack input, out Recipe recipe)
        {
            recipe = null;
            if (input == null) return null;
            if (input.IsSr && (type == "smelting" || type == "smoking" || type == "campfire_cooking"))
            {
                var c = CookSn(input.Actor);
                if (c != Identifiable.Id.NONE) { recipe = new Recipe { Type = type, Time = type == "smelting" ? 200 : type == "smoking" ? 100 : 600, Xp = 0.35f }; return new ItemStack(c, 1); }
            }
            foreach (var r in McData.Recipes)
            {
                if (r.Type != type || r.Ingredients.Count == 0) continue;
                if (Matches(input, r.Ingredients[0])) { recipe = r; return ItemStack.Of(r.Result, r.Count); }
            }
            return null;
        }

        public static int FuelTime(ItemStack s)
        {
            if (s == null) return 0;
            if (s.IsMc) return McData.Fuel.TryGetValue(s.Item.Id, out var t) ? t : 0;
            // Slime Rancher fuel: a fire plort burns like a blaze rod
            return s.Actor == Identifiable.Id.FIRE_PLORT ? 2400 : 0;
        }

        public static List<Recipe> Stonecutting(ItemStack input)
        {
            var list = new List<Recipe>();
            if (input == null) return list;
            foreach (var r in McData.Recipes)
                if (r.Type == "stonecutting" && r.Ingredients.Count > 0 && Matches(input, r.Ingredients[0])) list.Add(r);
            list.Sort((a, b) => string.CompareOrdinal(a.Result, b.Result));
            return list;
        }

        public static ItemStack Smithing(ItemStack template, ItemStack baseItem, ItemStack addition, out Recipe recipe)
        {
            recipe = null;
            foreach (var r in McData.Recipes)
            {
                if (r.Type != "smithing" || r.Result == null) continue;
                if (!Matches(template, r.Template) || !Matches(baseItem, r.Base) || !Matches(addition, r.Addition)) continue;
                recipe = r;
                var res = baseItem.Copy(1);
                res.Item = McData.Item(r.Result);
                return res;
            }
            return null;
        }
    }
}
