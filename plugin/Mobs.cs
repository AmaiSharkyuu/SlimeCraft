using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Minecraft's aquatic mobs living in Subnautica's ocean: cod, salmon, tropical fish, pufferfish, squid, glow squid,
    /// dolphins, turtles, axolotls, frogs, tadpoles, drowned, guardians and elder guardians. Their models are read from
    /// Minecraft's own model code (tools/mob_models.py), their health, size and loot from the dump and loot tables.
    /// Subnautica's own creatures can be summoned the same way.
    /// </summary>
    public static class Mobs
    {
        public static readonly List<Mob> All = new List<Mob>();
        private static JObject models;
        private static float nextSpawn;
        public static bool Spawning = true;

        public static readonly string[] Aquatic = { "cod", "salmon", "tropical_fish", "pufferfish", "squid", "glow_squid", "dolphin", "turtle", "axolotl", "frog", "tadpole", "drowned", "guardian", "elder_guardian" };

        public static JObject Model(string key)
        {
            if (models == null)
            {
                var p = System.IO.Path.Combine(McData.Root, "mobmodels.json");
                models = System.IO.File.Exists(p) ? JObject.Parse(System.IO.File.ReadAllText(p)) : new JObject();
            }
            return models[key] as JObject;
        }

        public static MPart Build(JObject m)
        {
            var root = new MPart("root");
            foreach (JObject part in (JArray)m["parts"]) root.Add(Part(part));
            return root;
        }

        private static MPart Part(JObject j)
        {
            var p = new MPart((string)j["name"]);
            var pv = j["pivot"].ToObject<float[]>(); var r = j["rot"].ToObject<float[]>();
            p.At(pv[0], pv[1], pv[2], r[0], r[1], r[2]);
            foreach (JObject c in (JArray)j["cuboids"])
            {
                var uv = c["uv"].ToObject<int[]>(); var pos = c["pos"].ToObject<float[]>(); var size = c["size"].ToObject<float[]>();
                p.Box(uv[0], uv[1], pos[0], pos[1], pos[2], size[0], size[1], size[2], (float)c["dil"], (bool)c["mirror"]);
            }
            foreach (JObject ch in (JArray)j["children"]) p.Add(Part(ch));
            return p;
        }

        public static bool IsMcMob(string type) => System.Array.IndexOf(Aquatic, type) >= 0;

        /// <summary>/summon and spawn eggs: a Minecraft aquatic mob, or any Subnautica creature by its Identifiable.Id name.</summary>
        public static bool Summon(string type, Vector3 at)
        {
            type = type.Replace("minecraft:", "").ToLowerInvariant();
            if (IsMcMob(type)) { Mob.Create(type, at); return true; }
            if (SrCreatures.TryGet(type, out var tt)) { UWE.CoroutineHost.StartCoroutine(SpawnSn(tt, at)); return true; }
            return false;
        }

        private static System.Collections.IEnumerator SpawnSn(Identifiable.Id tt, Vector3 at)
        {
            var task = CraftData.GetPrefabForTechTypeAsync(tt);
            yield return task;
            var prefab = task.GetResult();
            if (prefab == null) { Chat.System("§c" + McData.T("commands.summon.failed", "Impossible d'invoquer l'entité")); yield break; }
            var go = Object.Instantiate(prefab, at, Quaternion.Euler(0, Random.value * 360f, 0));
            go.SetActive(true);
            LargeWorldEntity.Register(go);
            CrafterLogic.NotifyCraftEnd(go, tt);
        }

        public static Mob Raycast(Vector3 origin, Vector3 dir, float max, out float dist)
        {
            dist = max; Mob best = null;
            var ray = new Ray(origin, dir);
            foreach (var m in All)
            {
                if (m == null || m.Dead) continue;
                if (m.Box.IntersectRay(ray, out float d) && d < dist && d >= 0) { dist = d; best = m; }
            }
            return best;
        }

        public static List<Mob> Near(Vector3 at, float r)
        {
            var l = new List<Mob>();
            foreach (var m in All) if (m != null && !m.Dead && (m.transform.position - at).sqrMagnitude < r * r) l.Add(m);
            return l;
        }

        public static void ClearAll()
        {
            foreach (var m in All.ToArray()) if (m != null) Object.Destroy(m.gameObject);
            All.Clear();
        }

        /// <summary>
        /// A light natural spawner, like Minecraft's water creature / ambient / monster categories around the player:
        /// fish near the surface, squid in the dark, dolphins and turtles now and then, drowned at night and guardians
        /// in the deep. Capped low so Subnautica's own fauna stays the main life of the ocean.
        /// </summary>
        public static void NaturalSpawn()
        {
            if (!Spawning || Time.time < nextSpawn || Player.main == null) return;
            nextSpawn = Time.time + 4f;
            int count = 0;
            foreach (var m in All) if (m != null && m.Natural) count++;
            if (count >= 10) return;
            var p = Player.main.transform.position;
            var at = p + Random.onUnitSphere * Random.Range(24f, 44f);
            if (!SrWorld.IsWater(at) || Physics.CheckSphere(at, 1f, SrWorld.SolidMask, QueryTriggerInteraction.Ignore)) return;
            float depth = -at.y;
            bool night = DayNightCycle.main != null && !DayNightCycle.main.IsDay();
            string type;
            float r = Random.value;
            if (depth > 300 && r < 0.08f) type = "guardian";
            else if (night && depth > 15 && r < 0.18f) type = "drowned";
            else if (depth > 100 && r < 0.35f) type = "glow_squid";
            else if (depth < 60 && r < 0.45f) type = Random.value < 0.5f ? "cod" : "salmon";
            else if (depth < 40 && r < 0.6f) type = "tropical_fish";
            else if (depth < 80 && r < 0.7f) type = "squid";
            else if (depth < 30 && r < 0.75f) type = "dolphin";
            else if (r < 0.8f) type = "pufferfish";
            else return;
            int group = type == "cod" || type == "salmon" || type == "tropical_fish" ? Random.Range(3, 7) : type == "dolphin" ? Random.Range(1, 3) : 1;
            for (int i = 0; i < group; i++)
            {
                var m = Mob.Create(type, at + Random.insideUnitSphere * 2f);
                m.Natural = true;
            }
        }
    }

    /// <summary>Subnautica creature names for /summon (Identifiable.Id names, case-insensitive) and their auto-completion.</summary>
    public static class SrCreatures
    {
        private static List<string> names;

        public static List<string> Names
        {
            get
            {
                if (names != null) return names;
                names = new List<string>();
                foreach (Identifiable.Id t in System.Enum.GetValues(typeof(Identifiable.Id)))
                {
                    if (t == Identifiable.Id.NONE) continue;
                    var s = t.ToString();
                    if (IsCreature(t)) names.Add(s.ToLowerInvariant());
                }
                names.Sort();
                return names;
            }
        }

        private static readonly HashSet<string> extra = new HashSet<string> { "seamoth", "exosuit", "cyclops", "rocketbase" };

        /// <summary>Creature-ish TechTypes: everything that is a fish, leviathan, critter or egg-hatched animal, plus vehicles.</summary>
        private static bool IsCreature(Identifiable.Id t)
        {
            var s = t.ToString();
            if (extra.Contains(s.ToLowerInvariant())) return true;
            string[] kinds = { "Leviathan", "Fish", "Peeper", "Bladderfish", "Boomerang", "Eyeye", "Garryfish", "GarryFish", "HoleFish", "Hoopfish", "Hoverfish", "Oculus", "Reginald", "Spadefish", "Spinefish",
                "Stalker", "Sandshark", "BoneShark", "Crash", "Biter", "Bleeder", "Blighter", "Gasopod", "Jellyray", "Mesmer", "Rabbitray", "Shocker", "Shuttlebug", "Skyray", "SpineEel",
                "Warper", "Cutefish", "Crabsnake", "CrabSquid", "LavaLizard", "LavaLarva", "Jumper", "CaveCrawler", "Floater", "GhostRay", "Crabsquid", "SeaDragon", "SeaTreader", "Reefback", "SeaEmperor", "GhostLeviathan", "ReaperLeviathan", "Lavaboomerang", "LavaBoomerang", "LavaEyeye" };
            if (s.StartsWith("Cooked") || s.StartsWith("Cured") || s.EndsWith("Egg") || s.EndsWith("EggUndiscovered") || s.EndsWith("Fragment") || s.EndsWith("Juvenile") && false) return false;
            foreach (var k in kinds) if (s.Contains(k)) return true;
            return false;
        }

        public static bool TryGet(string name, out Identifiable.Id t)
        {
            foreach (Identifiable.Id v in System.Enum.GetValues(typeof(Identifiable.Id)))
                if (v.ToString().ToLowerInvariant() == name.ToLowerInvariant()) { t = v; return true; }
            t = Identifiable.Id.NONE;
            return false;
        }
    }

    /// <summary>One Minecraft mob: model, health, AI, hurt / death, drops.</summary>
    public class Mob : McEntity
    {
        public string Type;
        public float Health, MaxHealth, Width = 0.5f, Height = 0.4f, AttackDamage, Speed = 0.1f;
        public bool Dead, Natural, Hostile;
        public bool Undead => Type == "drowned";
        public bool Arthropod => false;
        public bool Aquatic => Type != "drowned" && Type != "frog";
        private int hurtTime, deathTime, fireTicks, airTicks = 300, attackCooldown, ink;
        private float yaw, prevYaw, pitch, bodyAnim;
        private Vector3 wander; private int wanderTicks;
        private EntityModelInstance model;
        private EntityModelInstance outer;
        private Transform modelRoot;
        private Color baseColor = Color.white;
        private readonly Dictionary<string, Effects.Inst> effects = new Dictionary<string, Effects.Inst>();
        private int puff; private int puffTicks;
        private int beamTicks; private LineRenderer beam;
        private Mob target;
        private bool targetPlayer;

        public Bounds Box => new Bounds(transform.position + Vector3.up * Height / 2f, new Vector3(Width, Height, Width));

        public static Mob Create(string type, Vector3 at)
        {
            var go = new GameObject("mc " + type);
            go.transform.position = at;
            var m = go.AddComponent<Mob>();
            m.Type = type;
            m.Setup();
            Mobs.All.Add(m);
            return m;
        }

        private void OnDestroy() => Mobs.All.Remove(this);

        private void Setup()
        {
            var info = McData.Mobs.TryGetValue(Type, out var o) ? o : null;
            if (info != null)
            {
                Width = (float)info["width"]; Height = (float)info["height"];
                MaxHealth = info["generic.max_health"] != null ? (float)info["generic.max_health"] : 10f;
                AttackDamage = info["generic.attack_damage"] != null ? (float)info["generic.attack_damage"] : 0f;
                Speed = info["generic.movement_speed"] != null ? (float)info["generic.movement_speed"] : 0.1f;
            }
            Health = MaxHealth;
            Radius = Mathf.Max(0.1f, Mathf.Min(Width, Height) * 0.45f);
            Hostile = Type == "drowned" || Type == "guardian" || Type == "elder_guardian";
            yaw = Random.value * 360f;
            BuildModel();
        }

        private void BuildModel()
        {
            string key = Type, tex;
            float scale = 1f;
            switch (Type)
            {
                case "cod": tex = "entity/fish/cod"; break;
                case "salmon": tex = "entity/fish/salmon"; break;
                case "tropical_fish": key = Random.value < 0.5f ? "tropical_fish_a" : "tropical_fish_b"; tex = key == "tropical_fish_a" ? "entity/fish/tropical_a" : "entity/fish/tropical_b"; break;
                case "pufferfish": key = "pufferfish_small"; tex = "entity/fish/pufferfish"; break;
                case "squid": tex = "entity/squid/squid"; break;
                case "glow_squid": key = "squid"; tex = "entity/squid/glow_squid"; break;
                case "dolphin": tex = "entity/dolphin"; break;
                case "turtle": tex = "entity/turtle/big_sea_turtle"; break;
                case "axolotl": { string[] v = { "lucy", "wild", "gold", "cyan", "blue" }; tex = "entity/axolotl/axolotl_" + v[Random.Range(0, Random.value < 0.001f ? 5 : 4)]; break; }
                case "frog": tex = "entity/frog/temperate_frog"; break;
                case "tadpole": tex = "entity/tadpole/tadpole"; break;
                case "drowned": tex = "entity/zombie/drowned"; break;
                case "guardian": tex = "entity/guardian"; break;
                case "elder_guardian": key = "guardian"; tex = "entity/guardian_elder"; scale = 2.35f; break;
                default: tex = "entity/fish/cod"; break;
            }
            var json = Mobs.Model(key);
            if (json == null) return;
            var tw = json["tex"].ToObject<int[]>();
            var holder = new GameObject("holder").transform;
            holder.SetParent(transform, false);
            holder.localScale = Vector3.one * scale;
            modelRoot = holder;
            model = EntityModelInstance.Create(Mobs.Build(json), McTex.Get(tex), holder, 0, true, tw[0], tw[1]);
            if (Type == "glow_squid") { model.Mat.EnableKeyword("_EMISSION"); model.Mat.SetColor("_EmissionColor", Color.white * 0.6f); model.Mat.SetTexture("_EmissionMap", McTex.Get(tex)); }
            if (Type == "tropical_fish")
            {
                // TropicalFishEntity: a body colour and a pattern colour from the 16 dye colours
                int[] dyes = { 0xF9FFFE, 0xF9801D, 0xC74EBD, 0x3AB3DA, 0xFED83D, 0x80C71F, 0xF38BAA, 0x474F52, 0x9D9D97, 0x169C9C, 0x8932B8, 0x3C44AA, 0x835432, 0x5E7C16, 0xB02E26, 0x1D1D21 };
                model.Mat.color = ItemRender.Hex(dyes[Random.Range(0, 16)]);
                var pattern = McTex.Get((key == "tropical_fish_a" ? "entity/fish/tropical_a_pattern_" : "entity/fish/tropical_b_pattern_") + Random.Range(1, 7));
                outer = EntityModelInstance.Create(Mobs.Build(json), pattern, holder, 0, true, tw[0], tw[1]);
                outer.Mat.color = ItemRender.Hex(dyes[Random.Range(0, 16)]);
                outer.Go.transform.localScale = Vector3.one * 1.001f;
            }
            if (Type == "drowned")
            {
                var outerJson = Mobs.Model("drowned_outer");
                if (outerJson != null) outer = EntityModelInstance.Create(Mobs.Build(outerJson), McTex.Get("entity/zombie/drowned_outer_layer"), holder, 0, true, 64, 64);
            }
            baseColor = model.Mat.color;
            Sky.Apply(gameObject);
            foreach (var r in GetComponentsInChildren<Renderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        }

        // ------------------------------------------------------------------------------------------------ damage

        public bool Damage(float amount, string source, float knockback, Vector3 dir)
        {
            if (Dead) return false;
            if (hurtTime > 10) return false; // LivingEntity: half the 20-tick invulnerability
            Health -= amount;
            hurtTime = 20;
            if (knockback > 0)
            {
                // LivingEntity.takeKnockback
                var d = new Vector3(dir.x, 0, dir.z).normalized * knockback;
                Vel = new Vector3(Vel.x / 2f + d.x, InWater ? Vel.y : Mathf.Min(0.4f, Vel.y / 2f + knockback), Vel.z / 2f + d.z);
            }
            if (source == "player" || source == "arrow" || source == "thrown") { targetPlayer = true; }
            Sounds.Play("entity." + Type + ".hurt", transform.position, 1f, (Random.value - Random.value) * 0.2f + 1f);
            if (Type == "squid" || Type == "glow_squid") Ink();
            if (Type == "axolotl" && Health < MaxHealth / 2 && Random.value < 0.33f) { /* plays dead */ }
            if (Health <= 0) Die(source);
            return true;
        }

        public void SetOnFire(int seconds) { if (!InWater) fireTicks = Mathf.Max(fireTicks, seconds * 20); }

        public void AddEffect(string id, int dur, int amp)
        {
            if (McData.Effects.TryGetValue(id, out var def) && def.Instant)
            {
                bool undead = Undead;
                if (id == "instant_health") { if (undead) Damage(6 << amp, "magic", 0, Vector3.zero); else Health = Mathf.Min(MaxHealth, Health + (4 << amp)); }
                if (id == "instant_damage") { if (undead) Health = Mathf.Min(MaxHealth, Health + (4 << amp)); else Damage(6 << amp, "magic", 0, Vector3.zero); }
                return;
            }
            effects[id] = new Effects.Inst { Id = id, Duration = dur, Amplifier = amp };
        }

        private void Die(string source)
        {
            Dead = true;
            deathTime = 0;
            Sounds.Play("entity." + Type + ".death", transform.position);
            if (source == "player" || source == "arrow" || source == "thrown" || source == "explosion")
            {
                var held = Inventory.Held;
                var key = Type == "pufferfish" ? "pufferfish" : Type;
                if (McData.EntityLoot.TryGetValue(key, out var table))
                    foreach (var d in Loot.Roll(table, new Loot.Ctx { Tool = held, KilledByPlayer = true, OnFire = fireTicks > 0, Looting = held != null ? held.Level("looting") : 0 }))
                        ItemEntity.Spawn(d, transform.position + Vector3.up * Height / 2f, Random.insideUnitSphere * 0.1f);
                int xp = Hostile ? (Type == "elder_guardian" ? 10 : Type == "guardian" ? 10 : 5) : Random.Range(1, 4);
                XpOrb.Spawn(transform.position + Vector3.up * Height / 2f, xp);
                // the axolotl's gift: killing next to one grants Regeneration
                foreach (var m in Mobs.Near(transform.position, 20)) if (m.Type == "axolotl" && !m.Dead) { Effects.Add("regeneration", 100, 0); Effects.Remove("mining_fatigue"); break; }
            }
        }

        private void Ink()
        {
            ink = 10;
            for (int i = 0; i < 30; i++) Particles.Smoke(transform.position + Random.insideUnitSphere * 0.5f);
            Sounds.Play(Type == "glow_squid" ? "entity.glow_squid.squirt" : "entity.squid.squirt", transform.position);
            Vel += Random.onUnitSphere * 0.3f;
        }

        // ------------------------------------------------------------------------------------------------ AI

        protected override void Tick()
        {
            Age++;
            if (hurtTime > 0) hurtTime--;
            if (attackCooldown > 0) attackCooldown--;
            prevYaw = yaw;
            if (Dead)
            {
                if (++deathTime >= 20)
                {
                    for (int i = 0; i < 20; i++) Particles.Smoke(transform.position + Random.insideUnitSphere * Width);
                    Destroy(gameObject);
                }
                Vel *= 0.8f;
                MoveCollide();
                return;
            }
            var p = Player.main;
            if (p != null && Natural && (transform.position - p.transform.position).sqrMagnitude > 128 * 128) { Destroy(gameObject); return; }
            InWater = SrWorld.IsWater(transform.position + Vector3.up * Height * 0.5f);
            foreach (var e in new List<Effects.Inst>(effects.Values))
            {
                if (e.Id == "poison" && e.Duration % Mathf.Max(1, 25 >> e.Amplifier) == 0 && Health > 1) Damage(1, "magic", 0, Vector3.zero);
                if (e.Id == "regeneration" && e.Duration % Mathf.Max(1, 50 >> e.Amplifier) == 0) Health = Mathf.Min(MaxHealth, Health + 1);
                if (--e.Duration <= 0) effects.Remove(e.Id);
            }
            if (fireTicks > 0) { fireTicks--; if (fireTicks % 20 == 0) Damage(1, "fire", 0, Vector3.zero); if (InWater) fireTicks = 0; }

            bool fish = Type == "cod" || Type == "salmon" || Type == "tropical_fish" || Type == "pufferfish" || Type == "tadpole";
            if (fish || Type == "squid" || Type == "glow_squid" || Type == "dolphin" || Type == "guardian" || Type == "elder_guardian")
            {
                if (!InWater)
                {
                    // out of water: flop around and slowly suffocate (WaterCreatureEntity air)
                    if (--airTicks < -20) { airTicks = 0; Damage(2, "drown", 0, Vector3.zero); }
                    Vel.y -= 0.08f;
                    if (OnGround && Random.Range(0, 10) == 0) { Vel += new Vector3(Random.Range(-0.05f, 0.05f), 0.4f, Random.Range(-0.05f, 0.05f)); Sounds.Play("entity." + Type + ".flop", transform.position); }
                    MoveCollide();
                    Vel = new Vector3(Vel.x * 0.9f, Vel.y * 0.98f, Vel.z * 0.9f);
                    return;
                }
                airTicks = 300;
            }

            switch (Type)
            {
                case "drowned": Drowned(p); break;
                case "guardian": case "elder_guardian": Guardian(p); break;
                case "pufferfish": Pufferfish(p); Swim(0.04f); break;
                case "dolphin": Dolphin(p); break;
                default: Swim(Type == "turtle" ? 0.03f : Type == "squid" || Type == "glow_squid" ? 0.025f : 0.05f); break;
            }
            if (InWater) { Vel *= 0.9f; }
            else { Vel.y -= 0.08f; Vel = new Vector3(Vel.x * 0.91f, Vel.y * 0.98f, Vel.z * 0.91f); }
            MoveCollide();
            if (Vel.sqrMagnitude > 1e-5f)
            {
                float targetYaw = Mathf.Atan2(Vel.x, Vel.z) * Mathf.Rad2Deg;
                yaw = Mathf.MoveTowardsAngle(yaw, targetYaw, 10f);
                pitch = Mathf.Lerp(pitch, -Mathf.Asin(Mathf.Clamp(Vel.normalized.y, -1, 1)) * Mathf.Rad2Deg, 0.2f);
            }
            bodyAnim += Vel.magnitude * 4f;
        }

        /// <summary>SwimAroundGoal / FleeEntityGoal: wander in the water, flee a player that hurt them.</summary>
        private void Swim(float speed)
        {
            if (!InWater) return;
            var p = Player.main;
            if (targetPlayer && p != null && Type != "dolphin" && !Hostile)
            {
                var away = transform.position - p.transform.position;
                if (away.magnitude < 12f) { Vel += away.normalized * speed * 1.5f; return; }
                targetPlayer = false;
            }
            if (--wanderTicks <= 0 || Physics.Raycast(transform.position, wander, 2f, SrWorld.SolidMask, QueryTriggerInteraction.Ignore))
            {
                wanderTicks = Random.Range(40, 120);
                wander = Random.onUnitSphere;
                wander.y *= 0.3f;
                if (transform.position.y > Ocean.GetOceanLevel() - 1.5f) wander.y = -Mathf.Abs(wander.y) - 0.2f;
                wander.Normalize();
            }
            Vel += wander * speed * 0.3f;
        }

        private void Pufferfish(Player p)
        {
            // PufferfishEntity: puffs up when a player is within 2 blocks and stings those it touches
            bool near = p != null && (p.transform.position - transform.position).sqrMagnitude < 4f && !GameMode.Creative;
            if (near && puff < 2) { if (++puffTicks > 20) { puff++; puffTicks = 0; Sounds.Play("entity.puffer_fish.blow_up", transform.position); Rebuild(); } }
            else if (!near && puff > 0) { if (++puffTicks > 60) { puff--; puffTicks = 0; Sounds.Play("entity.puffer_fish.blow_out", transform.position); Rebuild(); } }
            if (puff > 0 && p != null && (p.transform.position - Vector3.up * 0.9f - transform.position).magnitude < 1.2f && attackCooldown == 0)
            {
                Health_Hurt(1 + puff, "mob");
                Effects.Add("poison", 60 * puff, 0);
                Sounds.Play("entity.puffer_fish.sting", transform.position);
                attackCooldown = 20;
            }
        }

        private void Rebuild()
        {
            var key = puff == 0 ? "pufferfish_small" : puff == 1 ? "pufferfish_medium" : "pufferfish_big";
            var json = Mobs.Model(key);
            if (json == null || model == null) return;
            Destroy(model.Go);
            model = EntityModelInstance.Create(Mobs.Build(json), McTex.Get("entity/fish/pufferfish"), modelRoot, 0, true, 32, 32);
        }

        private void Dolphin(Player p)
        {
            Swim(0.06f);
            if (p == null) return;
            var d = p.transform.position - transform.position;
            // DolphinEntity.SwimWithPlayerGoal: swims alongside a nearby swimming player and gives Dolphin's Grace
            if (d.magnitude < 10f && Motor.InWater)
            {
                Vel += d.normalized * 0.04f;
                if (Motor.Swimming && d.magnitude < 6f) Effects.Add("dolphins_grace", 100, 0);
            }
        }

        private void Drowned(Player p)
        {
            if (p == null || GameMode.Creative || !p.liveMixin.IsAlive()) { Swim(0.02f); return; }
            var target = p.transform.position - Vector3.up * (Motor.EyeHeight - 0.9f);
            var d = target - transform.position - Vector3.up * Height * 0.5f;
            if (d.magnitude > 35f) { Swim(0.02f); return; }
            // DrownedEntity: swims towards its target when in water, attacks in reach (ZombieAttackGoal)
            float sp = InWater ? 0.04f : Speed * 0.5f;
            Vel += new Vector3(d.x, InWater ? d.y : 0, d.z).normalized * sp;
            if (d.magnitude < 1.8f && attackCooldown == 0)
            {
                attackCooldown = 20;
                Health_Hurt(AttackDamage, "mob");
                Sounds.Play("entity.drowned.attack", transform.position, 1f, (Random.value - Random.value) * 0.2f + 1f);
                swingArm = 10;
            }
            if (Age % 80 == 0) Sounds.Play(InWater ? "entity.drowned.ambient_water" : "entity.drowned.ambient", transform.position);
        }

        private int swingArm;

        private void Guardian(Player p)
        {
            Swim(0.03f);
            if (p == null || GameMode.Creative || !p.liveMixin.IsAlive()) { beamTicks = 0; SetBeam(false); return; }
            var eye = p.transform.position;
            var from = transform.position + Vector3.up * Height * 0.5f;
            float dist = Vector3.Distance(eye, from);
            bool sees = dist < 16f && !Physics.Linecast(from, eye, SrWorld.SolidMask, QueryTriggerInteraction.Ignore);
            if (Type == "elder_guardian" && Age % 1200 == 0 && dist < 50f) { Effects.Add("mining_fatigue", 6000, 2); Sounds.Play("entity.elder_guardian.curse", null); }
            if (!sees) { beamTicks = 0; SetBeam(false); return; }
            // GuardianEntity.FireBeamGoal: 80 ticks of charging beam, then 6 damage (8 for the elder) plus the attack damage
            beamTicks++;
            if (beamTicks == 1) Sounds.Play("entity.guardian.attack", from);
            SetBeam(true, from, eye, beamTicks / 80f);
            if (beamTicks >= 80)
            {
                Health_Hurt(Type == "elder_guardian" ? 8f + AttackDamage : 6f + AttackDamage * 0.5f, "magic");
                beamTicks = 0;
                SetBeam(false);
            }
            yaw = Mathf.Atan2(eye.x - from.x, eye.z - from.z) * Mathf.Rad2Deg;
        }

        private void SetBeam(bool on, Vector3 a = default, Vector3 b = default, float t = 0)
        {
            if (!on) { if (beam != null) beam.enabled = false; return; }
            if (beam == null)
            {
                beam = gameObject.AddComponent<LineRenderer>();
                beam.positionCount = 2;
                beam.material = BlockWorld.MakeMat(2, McTex.Get("entity/guardian_beam"));
                beam.material.EnableKeyword("_EMISSION");
                beam.textureMode = LineTextureMode.Tile;
            }
            beam.enabled = true;
            beam.startWidth = beam.endWidth = 0.2f;
            var c = Color.Lerp(new Color(0.25f, 0.1f, 0.5f), new Color(1f, 0.9f, 0.3f), t * t);
            beam.material.color = c;
            beam.material.SetColor("_EmissionColor", c);
            beam.SetPosition(0, a); beam.SetPosition(1, b);
        }

        /// <summary>A mob hurts the player: Minecraft damage through Minecraft's armour.</summary>
        private void Health_Hurt(float dmg, string cause)
        {
            if (Health <= 0) return;
            SlimeCraft.Health.Damage(dmg, cause, gameObject);
            var push = Player.main.transform.position - transform.position;
            Motor.Vel += new Vector3(push.x, 0, push.z).normalized * 0.4f + Vector3.up * (Motor.InWater ? 0 : 0.2f);
        }

        // ------------------------------------------------------------------------------------------------ render

        protected override void Render(float partial)
        {
            transform.position = Vector3.Lerp(prevPos, curPos, partial);
            if (model == null) return;
            float t = Age + partial;
            float y = Mathf.LerpAngle(prevYaw, yaw, partial);
            var rot = Quaternion.Euler(0, y, 0);
            bool water = InWater;
            switch (Type)
            {
                case "cod": case "salmon": case "tropical_fish": case "pufferfish":
                    // CodEntityRenderer.setupTransforms: body wiggle, lying on its side out of water
                    rot = rot * Quaternion.AngleAxis(-4.3f * Mathf.Sin(0.6f * t) * (Type == "salmon" ? 1f : 1f), Vector3.up);
                    if (!water) rot = rot * Quaternion.AngleAxis(-90, Vector3.forward);
                    break;
                case "dolphin": case "guardian": case "elder_guardian": case "squid": case "glow_squid":
                    rot = rot * Quaternion.AngleAxis(pitch, Vector3.right);
                    break;
            }
            transform.rotation = rot;
            Animate(t, water);
            // red flash while hurt, tip over when dying
            var col = hurtTime > 10 || Dead ? new Color(1f, 0.4f, 0.4f) : baseColor;
            if (model.Mat.color != col) model.Mat.color = col;
            if (Dead && modelRoot != null) modelRoot.localRotation = Quaternion.AngleAxis(Mathf.Min(90f, Mathf.Sqrt((deathTime + partial - 1f) / 20f * 1.6f) * 90f), Vector3.forward);
        }

        private void Animate(float t, bool water)
        {
            var r = model.Root;
            switch (Type)
            {
                case "cod": { float f = water ? 1f : 1.5f; r.Find("tail_fin")?.Pose(0, -f * 0.45f * Mathf.Sin(0.6f * t), 0); break; }
                case "tropical_fish": { float f = water ? 1f : 1.5f; var tail = r.Find("tail"); tail?.Pose(0, -f * 0.45f * Mathf.Sin(0.6f * t), 0); if (outer != null) outer.Root.Find("tail")?.Pose(0, -f * 0.45f * Mathf.Sin(0.6f * t), 0); break; }
                case "salmon": { float f = water ? 1f : 1.3f, g = water ? 1f : 1.7f; r.Find("body_back")?.Pose(0, -f * 0.25f * Mathf.Sin(g * 0.6f * t), 0); break; }
                case "pufferfish":
                    r.Find("right_blue_fin")?.Pose(0, 0, -0.2f + 0.4f * Mathf.Sin(t * 0.2f));
                    r.Find("left_blue_fin")?.Pose(0, 0, 0.2f - 0.4f * Mathf.Sin(t * 0.2f));
                    break;
                case "squid": case "glow_squid":
                {
                    // SquidEntity tentacle angle: a stroke every ~ 20 ticks
                    float a = Mathf.Abs(Mathf.Sin(t * 0.15f)) * Mathf.PI * 0.25f;
                    for (int i = 0; i < 8; i++) { var tp = r.Find("tentacle" + i); if (tp != null) tp.Pose(a, tp.Yaw, 0); }
                    break;
                }
                case "dolphin":
                {
                    bool moving = Vel.sqrMagnitude > 1e-4f;
                    var body = r.Find("body");
                    if (moving)
                    {
                        body?.Pose(-0.05f - 0.05f * Mathf.Cos(t * 0.3f), 0, 0);
                        r.Find("tail")?.Pose(-0.1f * Mathf.Cos(t * 0.3f), 0, 0);
                        r.Find("tail_fin")?.Pose(-0.2f * Mathf.Cos(t * 0.3f), 0, 0);
                    }
                    break;
                }
                case "guardian": case "elder_guardian":
                {
                    float f = (1f - (beamTicks > 0 ? 0.5f : 0f));
                    var tail0 = r.Find("tail0"); tail0?.Pose(0, Mathf.Sin(t * 0.3f) * Mathf.PI * 0.05f, 0);
                    r.Find("tail1")?.Pose(0, Mathf.Sin(t * 0.3f) * Mathf.PI * 0.1f, 0);
                    r.Find("tail2")?.Pose(0, Mathf.Sin(t * 0.3f) * Mathf.PI * 0.15f, 0);
                    break;
                }
                case "turtle": case "axolotl": case "frog":
                {
                    float s = Mathf.Cos(bodyAnim * 0.6662f) * 0.6f;
                    r.Find("right_front_leg")?.Pose(0, 0, s); r.Find("left_front_leg")?.Pose(0, 0, -s);
                    r.Find("right_hind_leg")?.Pose(s * 0.5f, 0, 0); r.Find("left_hind_leg")?.Pose(-s * 0.5f, 0, 0);
                    r.Find("tail")?.Pose(0, Mathf.Sin(t * 0.3f) * 0.3f, 0);
                    break;
                }
                case "tadpole": r.Find("tail")?.Pose(0, -0.6f * Mathf.Sin(0.6f * t), 0); break;
                case "drowned":
                {
                    // ZombieEntityModel arms reaching forward (CrossbowPosing.meleeAttack), legs swimming / walking
                    float swing = swingArm > 0 ? swingArm / 10f : 0;
                    if (swingArm > 0) swingArm--;
                    float g = Mathf.Min(1f, Vel.magnitude * 4f);
                    float f = bodyAnim;
                    var ra = r.Find("right_arm"); var la = r.Find("left_arm");
                    float s1 = Mathf.Sin(swing * Mathf.PI), s2 = Mathf.Sin((1f - (1f - swing) * (1f - swing)) * Mathf.PI);
                    ra?.Pose(-Mathf.PI / 2f - s1 * 1.2f + s2 * 0.4f, -(0.1f - s1 * 0.6f), 0);
                    la?.Pose(-Mathf.PI / 2f - s1 * 1.2f + s2 * 0.4f, 0.1f - s1 * 0.6f, 0);
                    r.Find("right_leg")?.Pose(Mathf.Cos(f * 0.6662f) * 1.4f * g, 0, 0);
                    r.Find("left_leg")?.Pose(Mathf.Cos(f * 0.6662f + Mathf.PI) * 1.4f * g, 0, 0);
                    if (outer != null)
                    {
                        outer.Root.Find("right_arm")?.Pose(ra.Pitch, ra.Yaw, 0); outer.Root.Find("left_arm")?.Pose(la.Pitch, la.Yaw, 0);
                        outer.Root.Find("right_leg")?.Pose(Mathf.Cos(f * 0.6662f) * 1.4f * g, 0, 0); outer.Root.Find("left_leg")?.Pose(Mathf.Cos(f * 0.6662f + Mathf.PI) * 1.4f * g, 0, 0);
                    }
                    break;
                }
            }
        }

        /// <summary>Right click with an item on the mob: buckets catch fish and axolotls (FishEntity / Bucketable).</summary>
        public bool Interact(ItemStack held)
        {
            if (held.Is("water_bucket"))
            {
                string bucket = Type == "cod" ? "cod_bucket" : Type == "salmon" ? "salmon_bucket" : Type == "tropical_fish" ? "tropical_fish_bucket" : Type == "pufferfish" ? "pufferfish_bucket" : Type == "axolotl" ? "axolotl_bucket" : Type == "tadpole" ? "tadpole_bucket" : null;
                if (bucket == null) return false;
                Sounds.Play(Type == "axolotl" ? "item.bucket.fill_axolotl" : "item.bucket.fill_fish", transform.position);
                if (held.Count > 1) { held.Count--; Inventory.Add(ItemStack.Of(bucket)); } else Inventory.Held = ItemStack.Of(bucket);
                Destroy(gameObject);
                return true;
            }
            return false;
        }
    }
}
