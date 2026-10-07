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
    /// Subnautica's resources are Minecraft ingredients too: titanium works as an iron ingot, gold as a gold ingot,
    /// a gas pod as gunpowder, a stalker tooth as a bone, Subnautica fish as cod or salmon... A lone Subnautica item
    /// in the grid turns into its Minecraft counterpart, and Subnautica's fabricators accept the Minecraft
    /// counterparts in return (McToSn, used by SrBridge).
    /// </summary>
    public static class Crafting
    {
        public static readonly Dictionary<Identifiable.Id, string> SnToMc = new Dictionary<Identifiable.Id, string>
        {
            { Identifiable.Id.Titanium, "iron_ingot" }, { Identifiable.Id.TitaniumIngot, "iron_block" }, { Identifiable.Id.Copper, "copper_ingot" },
            { Identifiable.Id.Gold, "gold_ingot" }, { Identifiable.Id.Diamond, "diamond" }, { Identifiable.Id.Quartz, "quartz" }, { Identifiable.Id.Glass, "glass" },
            { Identifiable.Id.Kyanite, "lapis_lazuli" }, { Identifiable.Id.PrecursorIonCrystal, "amethyst_shard" }, { Identifiable.Id.Sulphur, "gunpowder" },
            { Identifiable.Id.GasPod, "gunpowder" }, { Identifiable.Id.CrashPowder, "gunpowder" }, { Identifiable.Id.StalkerTooth, "bone" },
            { Identifiable.Id.CreepvinePiece, "kelp" }, { Identifiable.Id.CreepvineSeedCluster, "glow_berries" },
            { Identifiable.Id.AcidMushroom, "red_mushroom" }, { Identifiable.Id.WhiteMushroom, "brown_mushroom" }, { Identifiable.Id.CoralChunk, "brain_coral" },
            { Identifiable.Id.JellyPlant, "sea_pickle" }, { Identifiable.Id.Melon, "melon_slice" }, { Identifiable.Id.SmallMelon, "melon_slice" },
            { Identifiable.Id.BulboTreePiece, "apple" }, { Identifiable.Id.PurpleVegetable, "beetroot" }, { Identifiable.Id.HangingFruit, "sweet_berries" },
            { Identifiable.Id.KooshChunk, "sponge" }, { Identifiable.Id.FiberMesh, "string" }, { Identifiable.Id.Salt, "sugar" }, { Identifiable.Id.Silicone, "slime_ball" },
            { Identifiable.Id.Peeper, "tropical_fish" }, { Identifiable.Id.Bladderfish, "pufferfish" }, { Identifiable.Id.Boomerang, "cod" }, { Identifiable.Id.Eyeye, "salmon" },
            { Identifiable.Id.GarryFish, "cod" }, { Identifiable.Id.HoleFish, "salmon" }, { Identifiable.Id.Hoopfish, "tropical_fish" }, { Identifiable.Id.Hoverfish, "cod" },
            { Identifiable.Id.Oculus, "salmon" }, { Identifiable.Id.Reginald, "cod" }, { Identifiable.Id.Spadefish, "salmon" }, { Identifiable.Id.Spinefish, "cod" },
            { Identifiable.Id.LavaBoomerang, "cod" }, { Identifiable.Id.LavaEyeye, "salmon" },
            { Identifiable.Id.CookedBoomerang, "cooked_cod" }, { Identifiable.Id.CookedGarryFish, "cooked_cod" }, { Identifiable.Id.CookedHoverfish, "cooked_cod" },
            { Identifiable.Id.CookedReginald, "cooked_cod" }, { Identifiable.Id.CookedSpinefish, "cooked_cod" }, { Identifiable.Id.CookedLavaBoomerang, "cooked_cod" },
            { Identifiable.Id.CookedEyeye, "cooked_salmon" }, { Identifiable.Id.CookedHoleFish, "cooked_salmon" }, { Identifiable.Id.CookedOculus, "cooked_salmon" },
            { Identifiable.Id.CookedSpadefish, "cooked_salmon" }, { Identifiable.Id.CookedLavaEyeye, "cooked_salmon" }, { Identifiable.Id.CookedPeeper, "cooked_cod" },
            { Identifiable.Id.CookedHoopfish, "cooked_cod" }, { Identifiable.Id.CookedBladderfish, "cooked_cod" },
        };

        /// <summary>Minecraft item -> the Subnautica ingredient it can stand in for in fabricators and the habitat builder.</summary>
        public static readonly Dictionary<string, Identifiable.Id> McToSn = new Dictionary<string, Identifiable.Id>
        {
            { "iron_ingot", Identifiable.Id.Titanium }, { "copper_ingot", Identifiable.Id.Copper }, { "gold_ingot", Identifiable.Id.Gold }, { "diamond", Identifiable.Id.Diamond },
            { "quartz", Identifiable.Id.Quartz }, { "glass", Identifiable.Id.Glass }, { "lapis_lazuli", Identifiable.Id.Kyanite }, { "gunpowder", Identifiable.Id.Sulphur },
            { "bone", Identifiable.Id.StalkerTooth }, { "kelp", Identifiable.Id.CreepvinePiece }, { "string", Identifiable.Id.FiberMesh },
            { "amethyst_shard", Identifiable.Id.PrecursorIonCrystal }, { "sugar", Identifiable.Id.Salt },
        };

        /// <summary>Subnautica raw fish -> Subnautica cooked fish in furnaces, smokers and campfires.</summary>
        private static Identifiable.Id CookSn(Identifiable.Id t)
        {
            var s = t.ToString();
            if (TechTypeExtensions.FromString("Cooked" + s, out var cooked, true)) return cooked;
            return Identifiable.Id.NONE;
        }

        public static bool Matches(ItemStack s, string[] options)
        {
            if (options == null || options.Length == 0) return s == null;
            if (s == null) return false;
            string id = s.IsMc ? s.Item.Id : SnToMc.TryGetValue(s.Actor, out var m) ? m : null;
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
                    if (s0 != null && s0.IsSr && SnToMc.TryGetValue(s0.Actor, out var mc0) && McData.Item(mc0) != null)
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
            // Subnautica fuels: biological matter burns like Minecraft's
            switch (s.Actor)
            {
                case Identifiable.Id.CreepvinePiece: return 200;
                case Identifiable.Id.SeaTreaderPoop: return 1600;
                case Identifiable.Id.BloodOil: case Identifiable.Id.Lubricant: case Identifiable.Id.Benzene: return 6400;
                case Identifiable.Id.CrashPowder: case Identifiable.Id.Sulphur: return 1600;
            }
            return 0;
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
