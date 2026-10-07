using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SlimeCraft
{
    public class FoodInfo
    {
        public int Hunger;
        public float Saturation;
        public bool Meat, Always, Snack;
        public List<EffectChance> Effects = new List<EffectChance>();
    }

    public class EffectChance
    {
        public string Effect;
        public int Duration, Amplifier;
        public float Chance;
    }

    /// <summary>One Minecraft item with the values Minecraft's own code reported (dumper/), never guessed.</summary>
    public class ItemDef
    {
        public string Id, Name, Class, Mineable, ArmorSlot, ArmorMaterial, Rarity;
        public int Stack, Durability, Enchantability, ToolLevel, Armor;
        public float ToolSpeed, Toughness, AttackDamage = 1f, AttackSpeed = 4f, KnockbackResistance;
        public bool Fireproof;
        public BlockDef Block;
        public FoodInfo Food;
        public string Model;
        public List<KeyValuePair<Dictionary<string, float>, string>> Overrides = new List<KeyValuePair<Dictionary<string, float>, string>>();
        public HashSet<string> Tags = new HashSet<string>();

        public bool IsTool => ToolSpeed > 0;
        public bool Damageable => Durability > 0;
        public bool Is(string cls) => Class == cls;
        public override string ToString() => Id;
    }

    /// <summary>A block type with every state Minecraft knows for it.</summary>
    public class BlockDef
    {
        public string Id, Name, Class, Sound, Layer = "solid";
        public float Hardness, Resistance, Slipperiness = 0.6f, Velocity = 1f, JumpVelocity = 1f;
        public bool ToolRequired, Opaque, Replaceable;
        public ItemDef Item;
        public int Tint = -1;
        public JObject Loot;
        public readonly List<KeyValuePair<string, string[]>> Props = new List<KeyValuePair<string, string[]>>();
        public readonly Dictionary<string, BState> States = new Dictionary<string, BState>();
        public BState Default;
        public HashSet<string> Tags = new HashSet<string>();
        public JToken BlockstateJson;

        public bool HasProp(string p) { foreach (var kv in Props) if (kv.Key == p) return true; return false; }

        /// <summary>The state with the given properties changed from `from` (missing combinations fall back to the default).</summary>
        public BState With(BState from, string prop, string value)
        {
            var d = new Dictionary<string, string>(from.Props) { [prop] = value };
            return Get(d) ?? from;
        }

        public BState Get(Dictionary<string, string> props)
        {
            var key = BState.KeyOf(this, props);
            if (States.TryGetValue(key, out var s)) return s;
            if (States.Count == 0 || Default == null) return null;
            // A block with too many states for the dump (redstone wire...): make the state on demand from the default's data.
            s = new BState { Block = this, Key = key, Props = new Dictionary<string, string>(props), Collision = Default.Collision, Outline = Default.Outline, Light = Default.Light };
            States[key] = s;
            return s;
        }

        public override string ToString() => Id;
    }

    public class BState
    {
        public BlockDef Block;
        public string Key;
        public Dictionary<string, string> Props = new Dictionary<string, string>();
        public Bounds[] Collision, Outline;
        public int Light;
        internal List<ModelRef> models;

        public string this[string p] => Props.TryGetValue(p, out var v) ? v : null;
        public BState With(string p, string v) => Block.With(this, p, v);

        public bool FullCube => Collision.Length == 1 && Collision[0].size == Vector3.one;

        public static string KeyOf(BlockDef b, Dictionary<string, string> props)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in b.Props)
            {
                if (!props.TryGetValue(kv.Key, out var v)) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(kv.Key).Append('=').Append(v);
            }
            return sb.ToString();
        }

        public override string ToString() => Block.Id + "[" + Key + "]";
    }

    public struct ModelRef
    {
        public string Model;
        public int X, Y;
        public bool UvLock;
    }

    public class Recipe
    {
        public string Id, Type, Serializer, Group, Result;
        public int Count = 1, Width, Height, Time;
        public float Xp;
        public List<string[]> Ingredients = new List<string[]>();
        public string[] Template, Base, Addition;
    }

    public class EnchantDef
    {
        public string Id, Name, Target;
        public int Max, Weight;
        public bool Treasure, Curse, Random;
        public int[] MinPower, MaxPower;
        public HashSet<string> Exclusive = new HashSet<string>(), Items = new HashSet<string>();
    }

    public class EffectDef
    {
        public string Id, Name, Category;
        public int Color;
        public bool Instant;
    }

    /// <summary>
    /// Everything read from plugin/assets/mc.json: Minecraft's blocks, items, models, recipes, tags, loot tables,
    /// enchantments, effects, sound events and French strings.
    /// </summary>
    public static class McData
    {
        public static readonly Dictionary<string, ItemDef> Items = new Dictionary<string, ItemDef>();
        public static readonly List<ItemDef> ItemList = new List<ItemDef>();
        public static readonly Dictionary<string, BlockDef> Blocks = new Dictionary<string, BlockDef>();
        public static readonly Dictionary<string, JObject> Models = new Dictionary<string, JObject>();
        public static readonly List<Recipe> Recipes = new List<Recipe>();
        public static readonly Dictionary<string, HashSet<string>> ItemTags = new Dictionary<string, HashSet<string>>();
        public static readonly Dictionary<string, HashSet<string>> BlockTags = new Dictionary<string, HashSet<string>>();
        public static readonly Dictionary<string, int> Fuel = new Dictionary<string, int>();
        public static readonly Dictionary<string, EnchantDef> Enchants = new Dictionary<string, EnchantDef>();
        public static readonly Dictionary<string, EffectDef> Effects = new Dictionary<string, EffectDef>();
        public static readonly Dictionary<string, List<EffectChance>> Potions = new Dictionary<string, List<EffectChance>>();
        public static readonly List<JObject> Brewing = new List<JObject>();
        public static readonly Dictionary<string, JObject> Mobs = new Dictionary<string, JObject>();
        public static readonly Dictionary<string, JObject> EntityLoot = new Dictionary<string, JObject>();
        public static readonly Dictionary<string, string> Lang = new Dictionary<string, string>();
        public static readonly Dictionary<string, JArray> SoundEvents = new Dictionary<string, JArray>();
        public static readonly Dictionary<string, JObject> Animations = new Dictionary<string, JObject>();
        public static bool Loaded;

        public static string Root => Path.Combine(Plugin.Dir, "assets");

        public static string T(string key, string fallback = null) => Lang.TryGetValue(key, out var v) ? v : fallback ?? key;

        // ------------------------------------------------------------------------------------------------ language

        public static string LangCode = "";
        public static string ForceLang; // set by the debug channel to test translations
        public static bool French => (LangCode.Length > 0 ? LangCode : GameLangCode()) == "fr_fr";

        /// <summary>Minecraft's own translation file for the language Slime Rancher is set to (English as a fallback).</summary>
        public static string GameLangCode()
        {
            if (!string.IsNullOrEmpty(ForceLang)) return ForceLang;
            try
            {
                switch (SRSingleton<GameContext>.Instance.MessageDirector.GetCultureLang())
                {
                    case MessageDirector.Lang.DE: return "de_de";
                    case MessageDirector.Lang.ES: return "es_es";
                    case MessageDirector.Lang.FR: return "fr_fr";
                    case MessageDirector.Lang.RU: return "ru_ru";
                    case MessageDirector.Lang.SV: return "sv_se";
                    case MessageDirector.Lang.ZH: return "zh_cn";
                    case MessageDirector.Lang.JA: return "ja_jp";
                    case MessageDirector.Lang.PT: return "pt_br";
                    case MessageDirector.Lang.KO: return "ko_kr";
                }
            }
            catch { }
            return "en_us";
        }

        /// <summary>Switches Minecraft's texts (item and block names, messages, screens) to Slime Rancher's language.</summary>
        public static void FollowGameLanguage()
        {
            if (!Loaded) return;
            var code = GameLangCode();
            if (code == LangCode) return;
            var p = Path.Combine(Root, "lang", code + ".json");
            if (!File.Exists(p)) { if (LangCode.Length > 0) return; p = Path.Combine(Root, "lang", "en_us.json"); code = "en_us"; }
            if (!File.Exists(p)) return;
            LangCode = code;
            var o = JObject.Parse(File.ReadAllText(p, System.Text.Encoding.UTF8));
            foreach (var kv in o) Lang[kv.Key] = (string)kv.Value;
            foreach (var b in Blocks.Values) b.Name = T("block.minecraft." + b.Id, b.Name);
            foreach (var i in ItemList) i.Name = T(Lang.ContainsKey("item.minecraft." + i.Id) ? "item.minecraft." + i.Id : "block.minecraft." + i.Id, i.Name);
            Plugin.Log.LogInfo("Minecraft texts in " + code);
        }

        /// <summary>SlimeCraft's own few words, in French or English (English for the other languages).</summary>
        public static string ModText(string key)
        {
            switch (key)
            {
                case "rightClick": return French ? "Clic droit" : "Right click";
                case "slimeStorage": return French ? "Stockage de Slime" : "Slime Storage";
                case "slimeRancher": return "Slime Rancher";
                case "missingAssets":
                    return French
                        ? "SlimeCraft : les textures et sons de Minecraft ne sont pas encore extraits.\nFerme le jeu et lance SlimeCraft-Assets.exe (dans BepInEx\\plugins\\SlimeCraft), il faut Minecraft 1.20.1."
                        : "SlimeCraft: Minecraft's textures and sounds have not been extracted yet.\nClose the game and run SlimeCraft-Assets.exe (in BepInEx\\plugins\\SlimeCraft); Minecraft 1.20.1 is needed.";
            }
            return key;
        }

        public static string Tf(string key, params object[] args)
        {
            var s = T(key);
            // Minecraft format strings use %s and %1$s.
            int ai = 0;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '%' && i + 1 < s.Length)
                {
                    if (s[i + 1] == 's') { sb.Append(ai < args.Length ? args[ai++] : ""); i++; continue; }
                    if (s[i + 1] == 'd') { sb.Append(ai < args.Length ? args[ai++] : ""); i++; continue; }
                    if (char.IsDigit(s[i + 1]) && i + 3 < s.Length && s[i + 2] == '$')
                    {
                        int n = s[i + 1] - '1';
                        sb.Append(n < args.Length ? args[n] : "");
                        i += 3;
                        continue;
                    }
                    if (s[i + 1] == '%') { sb.Append('%'); i++; continue; }
                }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        public static ItemDef Item(string id)
        {
            if (id == null) return null;
            if (id.StartsWith("minecraft:")) id = id.Substring(10);
            return Items.TryGetValue(id, out var d) ? d : null;
        }

        public static BlockDef Block(string id)
        {
            if (id == null) return null;
            if (id.StartsWith("minecraft:")) id = id.Substring(10);
            return Blocks.TryGetValue(id, out var d) ? d : null;
        }

        public static bool ItemHasTag(ItemDef d, string tag) => ItemTags.TryGetValue(tag, out var s) && s.Contains(d.Id);
        public static bool BlockHasTag(BlockDef d, string tag) => d != null && BlockTags.TryGetValue(tag, out var s) && s.Contains(d.Id);

        public static void Load()
        {
            if (Loaded) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var root = JObject.Parse(File.ReadAllText(Path.Combine(Root, "mc.json"), System.Text.Encoding.UTF8));

            foreach (var kv in (JObject)root["lang"]) Lang[kv.Key] = (string)kv.Value;
            foreach (var kv in (JObject)root["models"]) Models[kv.Key] = (JObject)kv.Value;
            foreach (var kv in (JObject)root["sounds"]) SoundEvents[kv.Key] = (JArray)kv.Value;
            foreach (var kv in (JObject)root["animations"]) Animations[kv.Key] = (JObject)kv.Value;
            foreach (var kv in (JObject)root["itemTags"]) ItemTags[kv.Key] = new HashSet<string>(kv.Value.ToObject<string[]>());
            foreach (var kv in (JObject)root["blockTags"]) BlockTags[kv.Key] = new HashSet<string>(kv.Value.ToObject<string[]>());
            foreach (var kv in (JObject)root["fuel"]) Fuel[kv.Key] = (int)kv.Value;
            foreach (var kv in (JObject)root["mobs"]) Mobs[kv.Key] = (JObject)kv.Value;
            foreach (var kv in (JObject)root["entityLoot"]) EntityLoot[kv.Key] = (JObject)kv.Value;

            // ---- shapes
            var shapes = new List<Bounds[]>();
            foreach (JArray s in (JArray)root["shapes"])
            {
                var bs = new Bounds[s.Count];
                for (int i = 0; i < s.Count; i++)
                {
                    var b = (JArray)s[i];
                    var min = new Vector3((float)b[0], (float)b[1], (float)b[2]);
                    var max = new Vector3((float)b[3], (float)b[4], (float)b[5]);
                    bs[i] = new Bounds((min + max) / 2f, max - min);
                }
                shapes.Add(bs);
            }

            // ---- blocks
            var names = (JObject)root["blockNames"];
            var layers = (JObject)root["layers"];
            var tints = (JObject)root["tints"];
            var loot = (JObject)root["loot"];
            var blockstates = (JObject)root["blockstates"];
            foreach (var kv in (JObject)root["blocks"])
            {
                var o = (JObject)kv.Value;
                var b = new BlockDef
                {
                    Id = kv.Key, Name = (string)names[kv.Key] ?? kv.Key, Class = (string)o["class"],
                    Hardness = (float)o["hardness"], Resistance = (float)o["resistance"], ToolRequired = (bool)o["toolRequired"],
                    Sound = (string)o["sound"], Opaque = (bool)o["opaque"], Replaceable = (bool)o["replaceable"],
                    Slipperiness = (float)o["slipperiness"], Velocity = (float)o["velocity"], JumpVelocity = (float)o["jumpVelocity"],
                };
                if (layers[kv.Key] != null) b.Layer = (string)layers[kv.Key];
                if (tints[kv.Key] != null) b.Tint = (int)tints[kv.Key];
                b.Loot = loot[kv.Key] as JObject;
                b.BlockstateJson = blockstates[kv.Key];
                foreach (var p in (JObject)o["props"]) b.Props.Add(new KeyValuePair<string, string[]>(p.Key, p.Value.ToObject<string[]>()));
                foreach (var st in (JObject)o["states"])
                {
                    var a = (JArray)st.Value;
                    var s = new BState { Block = b, Key = st.Key, Collision = shapes[(int)a[0]], Outline = shapes[(int)a[1]], Light = (int)a[2] };
                    if (st.Key.Length > 0)
                        foreach (var pair in st.Key.Split(','))
                        {
                            int eq = pair.IndexOf('=');
                            s.Props[pair.Substring(0, eq)] = pair.Substring(eq + 1);
                        }
                    b.States[st.Key] = s;
                }
                var def = (string)o["default"];
                if (!b.States.TryGetValue(def, out b.Default))
                {
                    // Too many states to dump: the default still needs a shape, assume a full cube.
                    b.Default = new BState { Block = b, Key = def, Collision = new[] { new Bounds(Vector3.one * 0.5f, Vector3.one) }, Outline = new[] { new Bounds(Vector3.one * 0.5f, Vector3.one) } };
                    if (def.Length > 0)
                        foreach (var pair in def.Split(','))
                        {
                            int eq = pair.IndexOf('=');
                            b.Default.Props[pair.Substring(0, eq)] = pair.Substring(eq + 1);
                        }
                    b.States[def] = b.Default;
                }
                Blocks[b.Id] = b;
            }
            foreach (var kv in BlockTags)
                foreach (var id in kv.Value)
                    if (Blocks.TryGetValue(id, out var b)) b.Tags.Add(kv.Key);

            // ---- items
            var inames = (JObject)root["names"];
            var imodels = (JObject)root["itemModels"];
            foreach (var kv in (JObject)root["items"])
            {
                var o = (JObject)kv.Value;
                var d = new ItemDef
                {
                    Id = kv.Key, Name = (string)inames[kv.Key] ?? kv.Key, Class = (string)o["class"],
                    Stack = (int)o["stack"], Durability = (int)o["durability"], Enchantability = (int)o["enchantability"],
                    Rarity = (string)o["rarity"], Fireproof = (bool)o["fireproof"],
                };
                if (o["block"] != null && Blocks.TryGetValue((string)o["block"], out var bd)) { d.Block = bd; if (bd.Item == null) bd.Item = d; }
                if (o["toolSpeed"] != null) { d.ToolSpeed = (float)o["toolSpeed"]; d.ToolLevel = (int)o["toolLevel"]; d.Mineable = (string)o["mineable"]; }
                if (o["armorSlot"] != null) d.ArmorSlot = (string)o["armorSlot"];
                if (o["armor"] != null) { d.Armor = (int)o["armor"]; d.Toughness = (float)o["toughness"]; d.ArmorMaterial = (string)o["armorMaterial"]; }
                var mh = o["attributes"]?["mainhand"];
                if (mh != null)
                {
                    if (mh["generic.attack_damage"] != null) d.AttackDamage = (float)mh["generic.attack_damage"];
                    if (mh["generic.attack_speed"] != null) d.AttackSpeed = (float)mh["generic.attack_speed"];
                }
                foreach (var slot in new[] { "head", "chest", "legs", "feet" })
                {
                    var a = o["attributes"]?[slot];
                    if (a?["generic.knockback_resistance"] != null) d.KnockbackResistance = (float)a["generic.knockback_resistance"];
                }
                var f = o["food"];
                if (f != null)
                {
                    d.Food = new FoodInfo { Hunger = (int)f["hunger"], Saturation = (float)f["saturation"], Meat = (bool)f["meat"], Always = (bool)f["always"], Snack = (bool)f["snack"] };
                    foreach (var e in (JArray)f["effects"])
                        d.Food.Effects.Add(new EffectChance { Effect = (string)e["effect"], Duration = (int)e["duration"], Amplifier = (int)e["amplifier"], Chance = (float)e["chance"] });
                }
                var im = imodels[kv.Key];
                if (im != null)
                {
                    d.Model = (string)im["model"];
                    foreach (var ov in (JArray)im["overrides"])
                    {
                        var pred = new Dictionary<string, float>();
                        foreach (var p in (JObject)ov["predicate"]) pred[p.Key] = (float)p.Value;
                        d.Overrides.Add(new KeyValuePair<Dictionary<string, float>, string>(pred, (string)ov["model"]));
                    }
                }
                Items[d.Id] = d;
                if (d.Id != "air") ItemList.Add(d);
            }
            foreach (var kv in ItemTags)
                foreach (var id in kv.Value)
                    if (Items.TryGetValue(id, out var d)) d.Tags.Add(kv.Key);

            // ---- recipes
            foreach (JObject o in (JArray)root["recipes"])
            {
                var r = new Recipe
                {
                    Id = (string)o["id"], Type = (string)o["type"], Serializer = (string)o["serializer"], Group = (string)o["group"],
                    Result = (string)o["result"], Count = o["count"] != null ? (int)o["count"] : 1,
                    Width = o["width"] != null ? (int)o["width"] : 0, Height = o["height"] != null ? (int)o["height"] : 0,
                    Xp = o["xp"] != null ? (float)o["xp"] : 0, Time = o["time"] != null ? (int)o["time"] : 0,
                };
                foreach (var ing in (JArray)o["ingredients"]) r.Ingredients.Add(ing.ToObject<string[]>());
                if (o["template"] != null) r.Template = o["template"].ToObject<string[]>();
                if (o["base"] != null) r.Base = o["base"].ToObject<string[]>();
                if (o["addition"] != null) r.Addition = o["addition"].ToObject<string[]>();
                Recipes.Add(r);
            }

            // ---- enchantments, effects, potions
            foreach (var kv in (JObject)root["enchantments"])
            {
                var o = (JObject)kv.Value;
                var e = new EnchantDef
                {
                    Id = kv.Key, Name = T((string)o["key"], kv.Key), Max = (int)o["max"], Weight = (int)o["weight"],
                    Treasure = (bool)o["treasure"], Curse = (bool)o["curse"], Random = (bool)o["random"], Target = (string)o["target"],
                    MinPower = o["minPower"].ToObject<int[]>(), MaxPower = o["maxPower"].ToObject<int[]>(),
                    Exclusive = new HashSet<string>(o["exclusive"].ToObject<string[]>()), Items = new HashSet<string>(o["items"].ToObject<string[]>()),
                };
                Enchants[e.Id] = e;
            }
            foreach (var kv in (JObject)root["effects"])
            {
                var o = (JObject)kv.Value;
                Effects[kv.Key] = new EffectDef { Id = kv.Key, Name = T((string)o["key"], kv.Key), Color = (int)o["color"], Category = (string)o["category"], Instant = (bool)o["instant"] };
            }
            foreach (var kv in (JObject)root["potions"])
            {
                var l = new List<EffectChance>();
                foreach (var e in (JArray)kv.Value) l.Add(new EffectChance { Effect = (string)e["effect"], Duration = (int)e["duration"], Amplifier = (int)e["amplifier"], Chance = 1 });
                Potions[kv.Key] = l;
            }
            foreach (JObject o in (JArray)root["brewing"]) Brewing.Add(o);

            Loaded = true;
            Plugin.Log.LogInfo($"Minecraft data: {Blocks.Count} blocks, {Items.Count} items, {Models.Count} models, {Recipes.Count} recipes, {SoundEvents.Count} sound events in {sw.ElapsedMilliseconds} ms");
        }

        // ------------------------------------------------------------------------------------------------ blockstates

        /// <summary>The models (with rotations) Minecraft draws for a state, from its blockstate file.</summary>
        public static List<ModelRef> ModelsFor(BState s)
        {
            if (s.models != null) return s.models;
            var list = new List<ModelRef>();
            var bs = s.Block.BlockstateJson as JObject;
            if (bs != null)
            {
                if (bs["variants"] is JObject variants)
                {
                    JToken pick = null;
                    foreach (var v in variants)
                    {
                        if (Matches(s, v.Key)) { pick = v.Value; break; }
                    }
                    if (pick == null) foreach (var v in variants) { pick = v.Value; break; }
                    if (pick != null) list.Add(Ref(pick is JArray arr ? arr[0] : pick));
                }
                else if (bs["multipart"] is JArray parts)
                {
                    foreach (JObject part in parts)
                    {
                        var when = part["when"];
                        if (when != null && !When(s, when)) continue;
                        var apply = part["apply"];
                        list.Add(Ref(apply is JArray arr ? arr[0] : apply));
                    }
                }
            }
            s.models = list;
            return list;
        }

        private static ModelRef Ref(JToken t) => new ModelRef
        {
            Model = ((string)t["model"]).Replace("minecraft:", ""),
            X = t["x"] != null ? (int)t["x"] : 0,
            Y = t["y"] != null ? (int)t["y"] : 0,
            UvLock = t["uvlock"] != null && (bool)t["uvlock"],
        };

        /// <summary>"facing=east,half=bottom" style keys; an empty key matches anything.</summary>
        private static bool Matches(BState s, string key)
        {
            if (string.IsNullOrEmpty(key)) return true;
            foreach (var pair in key.Split(','))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0) continue;
                if (s[pair.Substring(0, eq)] != pair.Substring(eq + 1)) return false;
            }
            return true;
        }

        private static bool When(BState s, JToken when)
        {
            if (when["OR"] is JArray or)
            {
                foreach (var c in or) if (When(s, c)) return true;
                return false;
            }
            if (when["AND"] is JArray and)
            {
                foreach (var c in and) if (!When(s, c)) return false;
                return true;
            }
            foreach (var kv in (JObject)when)
            {
                var v = s[kv.Key];
                bool any = false;
                foreach (var alt in ((string)kv.Value).Split('|')) if (alt == v) { any = true; break; }
                if (!any) return false;
            }
            return true;
        }
    }
}
