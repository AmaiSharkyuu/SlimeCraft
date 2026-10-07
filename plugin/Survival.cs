using System.Collections.Generic;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Minecraft's game modes, tied to Subnautica's: creative also switches Subnautica to its creative rules
    /// (no oxygen, no cost, no blueprints needed...), survival puts the save's own rules back.
    /// </summary>
    public static class GameMode
    {
        public static bool Creative;
        public static bool Spectator; // reserved
        private static GameModeOption savedMode = GameModeOption.None;
        private const GameModeOption CreativeFlags = GameModeOption.NoSurvival | GameModeOption.NoCost | GameModeOption.NoBlueprints | GameModeOption.NoEnergy | GameModeOption.NoPressure | GameModeOption.NoOxygen | GameModeOption.NoRadiation;

        public static void Set(bool creative, bool announce = true)
        {
            Creative = creative;
            GameModeUtils.GetGameMode(out var mode, out var cheats);
            if (creative)
            {
                if (savedMode == GameModeOption.None) savedMode = mode;
                GameModeUtils.SetGameMode(mode | CreativeFlags, cheats);
            }
            else
            {
                var back = savedMode != GameModeOption.None ? savedMode : (mode & ~CreativeFlags);
                if ((back & GameModeOption.NoSurvival) != 0 && (back & GameModeOption.NoCost) != 0) back = GameModeOption.Survival; // the save was creative itself
                GameModeUtils.SetGameMode(back, cheats);
                Motor.Flying = false;
            }
            Persist.Dirty = true;
            if (announce)
            {
                var name = McData.T(creative ? "gameMode.creative" : "gameMode.survival");
                Chat.System(McData.Tf("commands.gamemode.success.self", name));
            }
        }

        public static void Toggle() => Set(!Creative);
    }

    /// <summary>
    /// Minecraft health on top of Subnautica's: 20 Minecraft health points are Subnautica's 100, so a heart is 10.
    /// Damage from Minecraft (falls, walls, starving, mobs) goes through Minecraft's armour, Protection enchantments,
    /// Resistance and absorption first; damage from Subnautica (bites, heat, explosions) is converted to Minecraft
    /// points and goes through the same armour maths before Subnautica applies it.
    /// </summary>
    public static class Health
    {
        public const float Scale = 5f;
        public static float Absorption;
        public static int HurtTime, InvulnerableTicks;
        private static bool ours;
        public static string LastCause = "generic";

        public static LiveMixin Live => Player.main != null ? Player.main.liveMixin : null;
        public static float Hp => Live != null ? Live.health / Scale : 20f;
        public static float MaxHp => Live != null ? Live.maxHealth / Scale : 20f;

        public static void Heal(float mcHp)
        {
            if (Live == null || mcHp <= 0) return;
            Live.AddHealth(mcHp * Scale);
        }

        /// <summary>Minecraft damage by cause: fall, flyIntoWall, starve, drown, mob, player, explosion, fire, magic, generic.</summary>
        public static void Damage(float amount, string cause, GameObject dealer = null)
        {
            if (Live == null || amount <= 0 || GameMode.Creative && cause != "outOfWorld" && cause != "kill") return;
            float d = Reduce(amount, cause);
            if (d <= 0) return;
            LastCause = cause;
            ours = true;
            try { Live.TakeDamage(d * Scale, Player.main.transform.position, cause == "fire" ? DamageType.Fire : cause == "explosion" ? DamageType.Explosive : DamageType.Normal, dealer); }
            finally { ours = false; }
        }

        /// <summary>LivingEntity.applyArmorToDamage + modifyAppliedDamage (resistance, protection enchantments, absorption).</summary>
        public static float Reduce(float amount, string cause)
        {
            bool bypassArmor = cause == "fall" || cause == "flyIntoWall" || cause == "starve" || cause == "drown" || cause == "magic" || cause == "outOfWorld" || cause == "kill" || cause == "wither" || cause == "poison";
            float d = amount;
            if (!bypassArmor)
            {
                d = DamageLeft(d, Armor.Points, Armor.Toughness);
                Armor.Wear(amount);
            }
            if (cause == "kill" || cause == "outOfWorld") return d;
            int res = Effects.Amplifier("resistance");
            if (Effects.Has("resistance")) d = Mathf.Max(d * (25 - (res + 1) * 5) / 25f, 0f);
            if (cause == "fire" && Effects.Has("fire_resistance")) return 0;
            int epf = Armor.ProtectionEpf(cause);
            if (epf > 0) d = d * (1f - Mathf.Clamp(epf, 0, 20) / 25f);
            if (Absorption > 0)
            {
                float a = Mathf.Min(Absorption, d);
                Absorption -= a; d -= a;
            }
            return d;
        }

        public static float DamageLeft(float damage, float armor, float toughness)
        {
            float f = 2f + toughness / 4f;
            float g = Mathf.Clamp(armor - damage / f, armor * 0.2f, 20f);
            return damage * (1f - g / 25f);
        }

        /// <summary>Subnautica damage to the player: Minecraft's armour and enchantments reduce it, creative cancels it.</summary>
        internal static bool Incoming(LiveMixin lm, ref float damage, DamageType type)
        {
            if (ours || Player.main == null || lm != Player.main.liveMixin) return true;
            if (GameMode.Creative) return false;
            string cause;
            switch (type)
            {
                case DamageType.Heat: case DamageType.Fire: cause = "fire"; break;
                case DamageType.Explosive: cause = "explosion"; break;
                case DamageType.Starve: cause = "starve"; break;
                case DamageType.Poison: case DamageType.Acid: case DamageType.Radiation: case DamageType.Smoke: cause = "poison"; break;
                case DamageType.Pressure: case DamageType.Cold: case DamageType.Electrical: cause = "magic"; break;
                default: cause = "mob"; break;
            }
            if (type == DamageType.Starve && Player.main.GetComponent<OxygenManager>() != null && Player.main.GetOxygenAvailable() <= 0) cause = "drown";
            LastCause = cause;
            float mc = Reduce(damage / Scale, cause);
            damage = mc * Scale;
            return damage > 0;
        }

        public static void Tick()
        {
            if (HurtTime > 0) HurtTime--;
            if (InvulnerableTicks > 0) InvulnerableTicks--;
        }
    }

    [HarmonyPatch]
    internal static class HealthPatches
    {
        [HarmonyPatch(typeof(LiveMixin), nameof(LiveMixin.TakeDamage)), HarmonyPrefix]
        private static bool Take(LiveMixin __instance, ref float originalDamage, DamageType type, ref bool __result)
        {
            if (!Game.Active) return true;
            bool go = Health.Incoming(__instance, ref originalDamage, type);
            if (!go) { __result = false; return false; }
            return true;
        }

        [HarmonyPatch(typeof(LiveMixin), nameof(LiveMixin.TakeDamage)), HarmonyPostfix]
        private static void Took(LiveMixin __instance, float originalDamage, bool __result)
        {
            if (!Game.Active || Player.main == null || __instance != Player.main.liveMixin || originalDamage <= 0.01f) return;
            Health.HurtTime = 10;
            Sounds.Play("entity.player.hurt", Player.main.transform.position);
        }
    }

    /// <summary>Minecraft's armour slots: points, toughness, wear and Protection-type enchantments.</summary>
    public static class Armor
    {
        public static int Points
        {
            get
            {
                int n = 0;
                foreach (var a in Inventory.Armor) if (a != null && a.IsMc) n += a.Item.Armor;
                if (SnWorld.WearsReinforcedSuit()) n += 4;
                return Mathf.Min(n, 30);
            }
        }

        public static float Toughness
        {
            get { float t = 0; foreach (var a in Inventory.Armor) if (a != null && a.IsMc) t += a.Item.Toughness; return t; }
        }

        /// <summary>Each armour piece loses max(1, damage/4) durability (PlayerInventory.damageArmor).</summary>
        public static void Wear(float damage)
        {
            int d = Mathf.Max(1, (int)(damage / 4f));
            for (int i = 0; i < 4; i++)
            {
                var a = Inventory.Armor[i];
                if (a == null || !a.IsMc || a.Item.ArmorSlot == null || a.Is("elytra")) continue;
                Inventory.DamageStack(ref Inventory.Armor[i], d);
            }
        }

        /// <summary>ProtectionEnchantment.getProtectionAmount summed over the armour (EPF).</summary>
        public static int ProtectionEpf(string cause)
        {
            int epf = 0;
            foreach (var a in Inventory.Armor)
            {
                if (a == null) continue;
                int p = a.Level("protection"), fire = a.Level("fire_protection"), blast = a.Level("blast_protection"), proj = a.Level("projectile_protection"), fall = a.Level("feather_falling");
                epf += p;
                if (cause == "fire") epf += fire * 2;
                if (cause == "explosion") epf += blast * 2;
                if (cause == "projectile") epf += proj * 2;
                if (cause == "fall") epf += fall * 3;
            }
            return epf;
        }
    }

    /// <summary>
    /// Minecraft's HungerManager on Subnautica's food bar: Minecraft's 20 food points are Subnautica's 100 food.
    /// Saturation and exhaustion are Minecraft's own; spending exhaustion eats saturation first, then food,
    /// and a full stomach heals like in Minecraft (natural regeneration).
    /// </summary>
    public static class Hunger
    {
        public static float Saturation = 5f, Exhaustion;
        private static int foodTimer;
        public static int PrevFood = 20;

        private static Survival Sv => Player.main != null ? Player.main.GetComponent<Survival>() : null;
        public static int Food => Sv != null ? Mathf.Clamp(Mathf.CeilToInt(Sv.food / 5f), 0, 20) : 20;
        public static float Water => Sv != null ? Sv.water : 100f;

        public static bool CanEat(bool always) => always || Food < 20 || GameMode.Creative;

        public static void Exhaust(float e)
        {
            if (GameMode.Creative || e <= 0) return;
            Exhaustion = Mathf.Min(Exhaustion + e, 40f);
        }

        public static void Eat(int food, float satMod)
        {
            var sv = Sv;
            if (sv == null) return;
            float before = sv.food;
            sv.food = Mathf.Min(sv.food + food * 5f, 100f);
            Saturation = Mathf.Min(Saturation + food * satMod * 2f, Food);
        }

        public static void Drink(float water)
        {
            var sv = Sv;
            if (sv == null) return;
            sv.water = Mathf.Clamp(sv.water + water, 0, 100f);
        }

        public static void Tick()
        {
            var sv = Sv;
            if (sv == null || GameMode.Creative) return;
            PrevFood = Food;
            if (Exhaustion > 4f)
            {
                Exhaustion -= 4f;
                if (Saturation > 0f) Saturation = Mathf.Max(Saturation - 1f, 0f);
                else sv.food = Mathf.Max(sv.food - 5f, 0f);
            }
            var lm = Health.Live;
            bool canHeal = lm != null && lm.health > 0 && lm.health < lm.maxHealth;
            int food = Food;
            if (Saturation > 0f && canHeal && food >= 20)
            {
                if (++foodTimer >= 10)
                {
                    float f = Mathf.Min(Saturation, 6f);
                    Health.Heal(f / 6f);
                    Exhaust(f);
                    foodTimer = 0;
                }
            }
            else if (food >= 18 && canHeal)
            {
                if (++foodTimer >= 80)
                {
                    Health.Heal(1f);
                    Exhaust(6f);
                    foodTimer = 0;
                }
            }
            else if (food <= 0)
            {
                // Subnautica already starves the player on its own; Minecraft's starvation is left to it.
                foodTimer = 0;
            }
            else foodTimer = 0;
        }
    }

    /// <summary>Status effects (StatusEffectInstance): duration in ticks, amplifier, and their per-tick behaviour.</summary>
    public static class Effects
    {
        public class Inst { public string Id; public int Duration, Amplifier; public bool Ambient, ShowParticles = true; }
        public static readonly Dictionary<string, Inst> Active = new Dictionary<string, Inst>();
        private static Light nightLight;

        public static bool Has(string id) => Active.ContainsKey(id) || (id == "water_breathing" || id == "night_vision") && Active.ContainsKey("conduit_power");
        public static int Amplifier(string id) => Active.TryGetValue(id, out var e) ? e.Amplifier : -1;

        public static void Add(string id, int duration, int amp, bool ambient = false)
        {
            if (!McData.Effects.ContainsKey(id)) return;
            var def = McData.Effects[id];
            if (def.Instant) { Instant(id, amp); return; }
            if (Active.TryGetValue(id, out var cur) && (cur.Amplifier > amp || cur.Amplifier == amp && cur.Duration > duration)) return;
            Active[id] = new Inst { Id = id, Duration = duration, Amplifier = amp, Ambient = ambient };
            if (id == "absorption") Health.Absorption = Mathf.Max(Health.Absorption, 4f * (amp + 1));
            if (id == "health_boost") ApplyHealthBoost();
            if (id == "saturation") { Hunger.Eat(amp + 1, 1f); }
        }

        public static void Remove(string id)
        {
            Active.Remove(id);
            if (id == "absorption") Health.Absorption = 0;
            if (id == "health_boost") ApplyHealthBoost();
        }

        public static void Clear() { foreach (var k in new List<string>(Active.Keys)) Remove(k); }

        private static void Instant(string id, int amp)
        {
            if (id == "instant_health") Health.Heal(Mathf.Max(4 << amp, 0));
            else if (id == "instant_damage") Health.Damage(6 << amp, "magic");
            else if (id == "saturation") Hunger.Eat(amp + 1, 1f);
        }

        private static void ApplyHealthBoost()
        {
            // Subnautica's maximum health lives in shared data, so Health Boost's extra hearts are given as absorption instead
            if (Active.TryGetValue("health_boost", out var hb)) Health.Absorption = Mathf.Max(Health.Absorption, 4f * (hb.Amplifier + 1));
        }

        public static float SpeedMultiplier()
        {
            float m = 1f;
            if (Active.TryGetValue("speed", out var s)) m *= 1f + 0.2f * (s.Amplifier + 1);
            if (Active.TryGetValue("slowness", out var w)) m *= Mathf.Max(0f, 1f - 0.15f * (w.Amplifier + 1));
            return m;
        }

        public static float JumpBoost() => Active.TryGetValue("jump_boost", out var j) ? 0.1f * (j.Amplifier + 1) : 0f;
        public static int JumpBoostLevel() => Active.TryGetValue("jump_boost", out var j) ? j.Amplifier + 1 : 0;

        public static void Tick()
        {
            foreach (var e in new List<Inst>(Active.Values))
            {
                int a = e.Amplifier;
                switch (e.Id)
                {
                    case "regeneration": { int k = 50 >> a; if (k <= 0 || e.Duration % k == 0) Health.Heal(1f); break; }
                    case "poison": { int k = 25 >> a; if ((k <= 0 || e.Duration % k == 0) && Health.Hp > 1f) Health.Damage(1f, "poison"); break; }
                    case "wither": { int k = 40 >> a; if (k <= 0 || e.Duration % k == 0) Health.Damage(1f, "wither"); break; }
                    case "hunger": Hunger.Exhaust(0.005f * (a + 1)); break;
                    case "saturation": Hunger.Eat(a + 1, 1f); break;
                }
                if (--e.Duration <= 0 && e.Duration > -1000000) Remove(e.Id);
            }
            // Water Breathing and Conduit Power: Subnautica's oxygen stops running out.
            // Night Vision: a soft light around the head reaching into Subnautica's dark depths.
            bool nv = Has("night_vision");
            if (nv && nightLight == null && MainCamera.camera != null)
            {
                var go = new GameObject("mc night vision");
                go.transform.SetParent(MainCamera.camera.transform, false);
                nightLight = go.AddComponent<Light>();
                nightLight.type = LightType.Point;
                nightLight.range = 70f;
                nightLight.intensity = 1.2f;
                nightLight.color = new Color(0.8f, 0.9f, 1f);
                nightLight.shadows = LightShadows.None;
            }
            if (nightLight != null) nightLight.enabled = nv;
            bool invis = Has("invisibility");
            if (invis != invisSet)
            {
                invisSet = invis;
                if (invis) GameModeUtils.ActivateCheat(GameModeOption.NoAggression); else GameModeUtils.DeactivateCheat(GameModeOption.NoAggression);
            }
        }

        private static bool invisSet;

        public static JArray Save()
        {
            var a = new JArray();
            foreach (var e in Active.Values) a.Add(new JObject { ["id"] = e.Id, ["d"] = e.Duration, ["a"] = e.Amplifier });
            return a;
        }

        public static void Load(JToken t)
        {
            Active.Clear();
            if (t is JArray a) foreach (var e in a) Active[(string)e["id"]] = new Inst { Id = (string)e["id"], Duration = (int)e["d"], Amplifier = (int)e["a"] };
        }
    }

    [HarmonyPatch]
    internal static class OxygenPatches
    {
        // Water breathing / conduit power: breathing underwater like Minecraft's turtle helmet or potion.
        [HarmonyPatch(typeof(OxygenManager), nameof(OxygenManager.RemoveOxygen)), HarmonyPrefix]
        private static bool NoLoss(ref float __result, float amountToRemove)
        {
            if (!Game.Active || !(Effects.Has("water_breathing") || Effects.Has("conduit_power") || TurtleHelmet())) return true;
            __result = amountToRemove;
            return false;
        }

        private static bool TurtleHelmet()
        {
            // the turtle helmet gives 10 s of water breathing when going under; simplified to while the head is above water + 10 s
            var h = Inventory.ArmorIn("head");
            return h != null && h.Is("turtle_helmet") && Effects.Has("water_breathing");
        }
    }

    /// <summary>Experience: levels and the bar, with Minecraft's level cost curve.</summary>
    public static class Xp
    {
        public static int Level, Total;
        public static float Progress;

        public static int NextLevel(int level) => level >= 30 ? 112 + (level - 30) * 9 : level >= 15 ? 37 + (level - 15) * 5 : 7 + level * 2;

        public static void Add(int points)
        {
            if (points == 0) return;
            Progress += points / (float)NextLevel(Level);
            Total = Mathf.Max(0, Total + points);
            while (Progress < 0f)
            {
                float f = Progress * NextLevel(Level);
                if (Level > 0) { Level--; Progress = 1f + f / NextLevel(Level); }
                else { Level = 0; Progress = 0f; }
            }
            int before = Level;
            while (Progress >= 1f)
            {
                Progress = (Progress - 1f) * NextLevel(Level);
                Level++;
                Progress /= NextLevel(Level);
            }
            if (Level > before && Level % 5 == 0) Sounds.Play("entity.player.levelup", null, Mathf.Min(1f, Level / 30f) * 0.75f);
            Persist.Dirty = true;
        }

        public static void AddLevels(int levels)
        {
            Level = Mathf.Max(0, Level + levels);
            if (Level <= 0) { Level = 0; Progress = 0; Total = 0; }
            Persist.Dirty = true;
        }
    }

    /// <summary>Stats Minecraft keeps (only what gameplay uses) and the travel exhaustion of PlayerEntity.increaseTravelMotionStats.</summary>
    public static class Stats
    {
        public static void Moved(Vector3 d)
        {
            float cm = d.magnitude * 100f;
            if (Motor.Swimming) Hunger.Exhaust(0.01f * Mathf.Round(cm) * 0.01f);
            else if (Motor.InWater) Hunger.Exhaust(0.01f * Mathf.Round(new Vector2(d.x, d.z).magnitude * 100f) * 0.01f);
            else if (Motor.OnGround && Motor.Sprinting) Hunger.Exhaust(0.1f * Mathf.Round(new Vector2(d.x, d.z).magnitude * 100f) * 0.01f);
        }

        public static void Jumped() { }
    }

    /// <summary>Elytra: worn in the chest slot (Minecraft armour), glides while not broken.</summary>
    public static class Elytra
    {
        public static bool Worn
        {
            get { var c = Inventory.ArmorIn("chest"); return c != null && c.Is("elytra"); }
        }

        public static bool CanGlide()
        {
            var c = Inventory.ArmorIn("chest");
            return c != null && c.Is("elytra") && c.Damage < c.Item.Durability - 1;
        }

        public static void Wear()
        {
            var c = Inventory.Armor[2];
            if (c == null || !c.Is("elytra") || GameMode.Creative) return;
            if (c.Damage < c.Item.Durability - 1)
            {
                int unb = c.Level("unbreaking");
                if (unb == 0 || Random.Range(0, unb + 1) == 0) c.Damage++;
            }
        }
    }

    /// <summary>Enchantment helpers for movement and digging, with Subnautica's fins counting as Depth Strider.</summary>
    public static class Enchants
    {
        public static int Level(ItemStack s, string e) => s == null ? 0 : s.Level(e);

        public static float DepthStrider()
        {
            int lvl = Level(Inventory.ArmorIn("feet"), "depth_strider");
            if (SnWorld.HasFins()) lvl = Mathf.Max(lvl, 2);
            if (SnWorld.HasUltraGlideFins()) lvl = 3;
            return lvl;
        }
    }

    /// <summary>Sleeping in a Minecraft bed: like Subnautica's bed, it skips the night.</summary>
    public static class Sleep
    {
        public static bool Try(Vector3Int c, BState s)
        {
            var dnc = DayNightCycle.main;
            if (dnc == null) return false;
            if (dnc.IsDay())
            {
                Chat.ActionBar(McData.T("block.minecraft.bed.no_sleep"));
                return true;
            }
            Chat.ActionBar(McData.T("block.minecraft.set_spawn", "Point de réapparition défini"));
            Persist.Spawn = BlockWorld.Center(c) + Vector3.up;
            // Subnautica's day lasts 1200 s; wake up at the same morning hour Subnautica's own bed does.
            float skip = Mathf.Repeat(0.3f - dnc.GetDayScalar(), 1f) * 1200f;
            dnc.SkipTime(skip, 3f);
            return true;
        }
    }
}
