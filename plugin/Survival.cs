using System.Collections.Generic;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>Minecraft's game modes: survival and creative (F3+F4 or /gamemode), saved with each Slime Rancher game.</summary>
    public static class GameMode
    {
        public static bool Creative;
        public static bool Spectator; // reserved

        public static void Set(bool creative, bool announce = true)
        {
            Creative = creative;
            if (!creative) Motor.Flying = false;
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
    /// Minecraft health on top of Slime Rancher's: 20 Minecraft health points are Beatrix's 100, so a heart is 10.
    /// Damage from Minecraft (falls, walls, starving, mobs) goes through Minecraft's armour, Protection enchantments,
    /// Resistance and absorption first; damage from Slime Rancher (tarr bites, rad, explosions) is converted to
    /// Minecraft points and goes through the same armour maths before Slime Rancher applies it.
    /// </summary>
    public static class Health
    {
        public const float Scale = 5f;
        public static float Absorption;
        public static int HurtTime, InvulnerableTicks;
        private static bool ours;
        private static float healBuffer;
        public static string LastCause = "generic";

        public static PlayerState State => SRSingleton<SceneContext>.Instance != null ? SRSingleton<SceneContext>.Instance.PlayerState : null;
        public static float Hp => State != null ? State.GetCurrHealth() / Scale : 20f;
        public static float MaxHp => State != null ? State.GetMaxHealth() / Scale : 20f;

        public static void Heal(float mcHp)
        {
            var st = State;
            if (st == null || mcHp <= 0) return;
            // Slime Rancher heals in whole points; keep the fractions of Minecraft's half-heart heals until they add up.
            healBuffer += mcHp * Scale;
            int whole = (int)healBuffer;
            if (whole <= 0) return;
            healBuffer -= whole;
            st.Heal(whole);
        }

        /// <summary>Minecraft damage by cause: fall, flyIntoWall, starve, drown, mob, player, explosion, fire, magic, generic.</summary>
        public static void Damage(float amount, string cause, GameObject dealer = null)
        {
            var pl = SrWorld.Player;
            if (pl == null || amount <= 0 || GameMode.Creative && cause != "outOfWorld" && cause != "kill") return;
            float d = Reduce(amount, cause);
            if (d <= 0) return;
            LastCause = cause;
            ours = true;
            try
            {
                var pd = pl.GetComponent<PlayerDamageable>();
                int hp = Mathf.Max(1, Mathf.RoundToInt(d * Scale));
                if (pd != null) pd.Damage(hp, dealer); else State?.Damage(hp, dealer);
            }
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

        /// <summary>Slime Rancher damage to the player: Minecraft's armour and enchantments reduce it, creative cancels it.</summary>
        internal static bool Incoming(ref int damage, GameObject source)
        {
            if (ours) return true;
            if (GameMode.Creative) return false;
            string cause = "mob";
            if (source != null)
            {
                var n = source.name.ToLowerInvariant();
                if (n.Contains("fire") || n.Contains("lava")) cause = "fire";
                else if (n.Contains("boom") || n.Contains("explo")) cause = "explosion";
            }
            LastCause = cause;
            float mc = Reduce(damage / Scale, cause);
            damage = Mathf.RoundToInt(mc * Scale);
            return damage > 0;
        }

        /// <summary>Minecraft's air supply in ticks (300 = ten bubbles); drowning hurts 2 every second once it runs out.</summary>
        public static int Air = 300;

        public static void Tick()
        {
            if (HurtTime > 0) HurtTime--;
            if (InvulnerableTicks > 0) InvulnerableTicks--;
            if (Motor.Submerged && !Effects.Has("water_breathing") && !Effects.Has("conduit_power") && !GameMode.Creative)
            {
                int resp = Enchants.Level(Inventory.ArmorIn("head"), "respiration");
                if (resp == 0 || Random.Range(0, resp + 1) == 0) Air--;
                if (Air <= -20) { Air = 0; Damage(2f, "drown"); }
            }
            else if (Air < 300) Air = Mathf.Min(300, Air + 4);
        }
    }

    [HarmonyPatch]
    internal static class HealthPatches
    {
        [HarmonyPatch(typeof(PlayerDamageable), nameof(PlayerDamageable.Damage)), HarmonyPrefix]
        private static bool Take(ref int healthLoss, GameObject source, ref bool __result)
        {
            if (!Game.Active) return true;
            if (!Health.Incoming(ref healthLoss, source)) { __result = false; return false; }
            return true;
        }

        [HarmonyPatch(typeof(PlayerDamageable), nameof(PlayerDamageable.Damage)), HarmonyPostfix]
        private static void Took(int healthLoss)
        {
            if (!Game.Active || healthLoss <= 0) return;
            Health.HurtTime = 10;
            Sounds.Play("entity.player.hurt", SrWorld.PlayerPos);
        }

        // Creative players cannot be hurt by anything that goes straight to Beatrix's health either.
        [HarmonyPatch(typeof(PlayerState), nameof(PlayerState.Damage)), HarmonyPrefix]
        private static bool NoStateDamage(ref bool __result)
        {
            if (Game.Active && GameMode.Creative) { __result = false; return false; }
            return true;
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
                if (SrWorld.WearsReinforcedSuit()) n += 4;
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
    /// Minecraft's HungerManager: 20 food points, saturation and exhaustion, healing on a full stomach and starving
    /// on an empty one. Slime Rancher's fruits and veggies count as food, next to Minecraft's own.
    /// </summary>
    public static class Hunger
    {
        public static float Saturation = 5f, Exhaustion;
        private static int foodTimer;
        public static int PrevFood = 20;
        public static int FoodLevel = 20;

        public static int Food => Mathf.Clamp(FoodLevel, 0, 20);
        public static float Water => 100f;

        public static bool CanEat(bool always) => always || Food < 20 || GameMode.Creative;

        public static void Exhaust(float e)
        {
            if (GameMode.Creative || e <= 0) return;
            Exhaustion = Mathf.Min(Exhaustion + e, 40f);
        }

        public static void Eat(int food, float satMod)
        {
            FoodLevel = Mathf.Min(FoodLevel + food, 20);
            Saturation = Mathf.Min(Saturation + food * satMod * 2f, FoodLevel);
        }

        public static void Drink(float water) { }

        public static void Tick()
        {
            if (GameMode.Creative) return;
            PrevFood = Food;
            if (Exhaustion > 4f)
            {
                Exhaustion -= 4f;
                if (Saturation > 0f) Saturation = Mathf.Max(Saturation - 1f, 0f);
                else FoodLevel = Mathf.Max(FoodLevel - 1, 0);
            }
            bool canHeal = Health.Hp > 0 && Health.Hp < Health.MaxHp;
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
                // Minecraft's normal difficulty: starving takes you down to half a heart... here, to 5 hearts.
                if (++foodTimer >= 80)
                {
                    if (Health.Hp > 10f) Health.Damage(1f, "starve");
                    foodTimer = 0;
                }
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
            // Beatrix's maximum health belongs to her upgrades, so Health Boost's extra hearts are given as absorption instead
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
            // Night Vision: a soft light around the head reaching into the night and the caves.
            bool nv = Has("night_vision");
            if (nv && nightLight == null && Camera.main != null)
            {
                var go = new GameObject("mc night vision");
                go.transform.SetParent(Camera.main.transform, false);
                nightLight = go.AddComponent<Light>();
                nightLight.type = LightType.Point;
                nightLight.range = 70f;
                nightLight.intensity = 1.2f;
                nightLight.color = new Color(0.8f, 0.9f, 1f);
                nightLight.shadows = LightShadows.None;
            }
            if (nightLight != null) nightLight.enabled = nv;
        }

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

    /// <summary>Enchantment helpers for movement and digging.</summary>
    public static class Enchants
    {
        public static int Level(ItemStack s, string e) => s == null ? 0 : s.Level(e);

        public static float DepthStrider()
        {
            return Level(Inventory.ArmorIn("feet"), "depth_strider");
        }
    }

    /// <summary>Sleeping in a Minecraft bed: like Slime Rancher's ranch house, it fast-forwards to the morning.</summary>
    public static class Sleep
    {
        public static bool Try(Vector3Int c, BState s)
        {
            var td = SRSingleton<SceneContext>.Instance?.TimeDirector;
            if (td == null) return false;
            float h = td.CurrHour();
            if (h >= 6f && h < 19f)
            {
                Chat.ActionBar(McData.T("block.minecraft.bed.no_sleep"));
                return true;
            }
            Chat.ActionBar(McData.T("block.minecraft.set_spawn", "Respawn point set"));
            Persist.Spawn = BlockWorld.Center(c) + Vector3.up;
            td.FastForwardTo(td.GetHourAfter(h >= 19f ? 1 : 0, 6f));
            return true;
        }
    }
}
