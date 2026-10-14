using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Minecraft's projectiles in Subnautica's world: arrows (bow, crossbow, Power / Punch / Flame / Infinity /
    /// Multishot / Piercing), the trident (Loyalty brings it back, Riptide launches the player through the ocean,
    /// Impaling hurts every sea creature), snowballs, eggs, ender pearls, splash / lingering potions, bottles o'
    /// enchanting, firework rockets and primed TNT. They hit Minecraft blocks, Minecraft mobs and Subnautica's
    /// creatures (through their LiveMixin, 1 Minecraft point = 5 Subnautica health).
    /// </summary>
    public static class Projectiles
    {
        /// <summary>BowItem.getPullProgress.</summary>
        public static float PullProgress(int ticks)
        {
            float f = ticks / 20f;
            f = (f * f + f * 2f) / 3f;
            return Mathf.Min(f, 1f);
        }

        public static void ShootBow(ItemStack bow, int used)
        {
            float f = PullProgress(used);
            if (f < 0.1f) return;
            bool infinite = GameMode.Creative || bow.Level("infinity") > 0;
            ItemStack ammo = FindAmmo();
            if (ammo == null && !infinite) return;
            if (ammo == null) ammo = ItemStack.Of("arrow");
            var cam = Camera.main.transform;
            var a = Arrow.Create(ammo.Copy(1), cam.position - Vector3.up * 0.1f, cam.forward, f * 3f, 1f);
            a.Crit = f == 1f;
            int power = bow.Level("power");
            if (power > 0) a.Damage += power * 0.5f + 0.5f;
            a.Punch = bow.Level("punch");
            if (bow.Level("flame") > 0) a.Fire = true;
            a.CanPickup = !(infinite && ammo.Is("arrow")) && !GameMode.Creative;
            Sounds.Play("entity.arrow.shoot", cam.position, 1f, 1f / (Random.Range(0.8f, 1.2f)) + f * 0.5f);
            Inventory.DamageHeld(1);
            if (!(infinite && ammo.Is("arrow")) && !GameMode.Creative) Inventory.Remove(s => s.SameItem(ammo), 1);
        }

        private static ItemStack FindAmmo()
        {
            if (Inventory.Offhand != null && (Inventory.Offhand.Is("arrow") || Inventory.Offhand.Is("tipped_arrow") || Inventory.Offhand.Is("spectral_arrow"))) return Inventory.Offhand;
            foreach (var s in Inventory.Main) if (s != null && (s.Is("arrow") || s.Is("tipped_arrow") || s.Is("spectral_arrow"))) return s;
            return null;
        }

        public static void ShootCrossbow(ItemStack cb)
        {
            var cam = Camera.main.transform;
            int n = cb.Level("multishot") > 0 ? 3 : 1;
            for (int i = 0; i < n; i++)
            {
                var dir = Quaternion.AngleAxis(i == 0 ? 0 : i == 1 ? -10 : 10, cam.up) * cam.forward;
                if (cb.Flight > 0) { Firework(cam.position, 1, dir * 1.6f); continue; }
                var a = Arrow.Create(ItemStack.Of("arrow"), cam.position - Vector3.up * 0.1f, dir, 3.15f, 1f);
                a.Crit = true;
                a.Pierce = cb.Level("piercing");
                a.CanPickup = i == 0 && !GameMode.Creative;
            }
            Sounds.Play("item.crossbow.shoot", cam.position);
        }

        public static void ThrowTrident(ItemStack t)
        {
            var cam = Camera.main.transform;
            int riptide = t.Level("riptide");
            Inventory.DamageHeld(1);
            if (riptide > 0)
            {
                // TridentItem.onStoppedUsing with Riptide: the player is launched along the look
                float n = 3f * ((1f + riptide) / 4f);
                Motor.Vel += cam.forward * n;
                if (Motor.OnGround) Motor.Vel.y += 1.1999999f * 0.05f;
                Riptide.Start(20);
                Sounds.Play(riptide >= 3 ? "item.trident.riptide_3" : riptide == 2 ? "item.trident.riptide_2" : "item.trident.riptide_1", cam.position);
                return;
            }
            var a = Arrow.Create(t.Copy(1), cam.position - Vector3.up * 0.1f, cam.forward, 2.5f, 1f);
            a.Trident = true;
            a.Damage = 8f;
            a.Loyalty = t.Level("loyalty");
            a.CanPickup = !GameMode.Creative;
            Sounds.Play("item.trident.throw", cam.position);
            if (!GameMode.Creative) Inventory.Held = null;
        }

        public static void Throw(ItemStack s)
        {
            var cam = Camera.main.transform;
            var id = s.Item.Id;
            float speed = id == "experience_bottle" ? 0.7f : id.EndsWith("potion") ? 0.5f : 1.5f;
            float pitchOffset = id == "experience_bottle" ? -20f : id.EndsWith("potion") ? -20f : 0f;
            var dir = Quaternion.AngleAxis(pitchOffset, cam.right) * cam.forward;
            var t = Thrown.Create(s.Copy(1), cam.position - Vector3.up * 0.1f, dir, speed);
            Sounds.Play(id == "snowball" ? "entity.snowball.throw" : id == "egg" ? "entity.egg.throw" : id == "ender_pearl" ? "entity.ender_pearl.throw" : id == "experience_bottle" ? "entity.experience_bottle.throw" : "entity.splash_potion.throw",
                cam.position, 0.5f, 0.4f / (Random.value * 0.4f + 0.8f));
        }

        public static void Firework(Vector3 at, int flight, Vector3? vel = null)
        {
            var go = new GameObject("firework");
            go.transform.position = at;
            var f = go.AddComponent<FireworkEntity>();
            f.Life = 10 * (Mathf.Max(1, flight) + 1) + Random.Range(0, 6) + Random.Range(0, 7);
            f.Vel = vel ?? new Vector3(Random.Range(-0.05f, 0.05f) * 0.115f, 0.05f, Random.Range(-0.05f, 0.05f) * 0.115f);
            f.Straight = vel.HasValue;
            Sounds.Play("entity.firework_rocket.launch", at, 3f);
        }

        public static void Tnt(Vector3 at)
        {
            var go = new GameObject("primed tnt");
            go.transform.position = at;
            var t = go.AddComponent<TntEntity>();
            t.Radius = 0.49f;
            float a = Random.value * Mathf.PI * 2f;
            t.Vel = new Vector3(-Mathf.Sin(a) * 0.02f, 0.2f, -Mathf.Cos(a) * 0.02f);
            Sounds.Play("entity.tnt.primed", at);
        }

        /// <summary>Explosion: Minecraft blocks broken by their blast resistance, entities hurt by exposure (Explosion.collectBlocksAndDamageEntities).</summary>
        public static void Explode(Vector3 at, float power)
        {
            Sounds.Play("entity.generic.explode", at, 4f, (1f + (Random.value - Random.value) * 0.2f) * 0.7f);
            Particles.Explosion(at, power);
            bool water = SrWorld.IsWater(at);
            if (!water)
            {
                SrCarve.Blast(at, power);
                // rays out from the centre, each losing intensity with every block's resistance
                var broken = new HashSet<Vector3Int>();
                for (int j = 0; j < 16; j++)
                    for (int k = 0; k < 16; k++)
                        for (int l = 0; l < 16; l++)
                        {
                            if (j != 0 && j != 15 && k != 0 && k != 15 && l != 0 && l != 15) continue;
                            var d = new Vector3(j / 15f * 2f - 1f, k / 15f * 2f - 1f, l / 15f * 2f - 1f).normalized;
                            float h = power * (0.7f + Random.value * 0.6f);
                            var p = at;
                            while (h > 0f)
                            {
                                var c = BlockWorld.CellAt(p);
                                var s = BlockWorld.Get(c);
                                if (s != null) { h -= (s.Block.Resistance + 0.3f) * 0.3f; if (h > 0) broken.Add(c); }
                                p += d * 0.3f;
                                h -= 0.22500001f;
                            }
                        }
                foreach (var c in broken) if (BlockWorld.Has(c)) Interaction.BreakBlock(c, null, true, true);
            }
            float q = power * 2f;
            // the player
            if (SrWorld.Player != null)
            {
                float dist = Vector3.Distance(SrWorld.Feet + Vector3.up * 0.9f, at) / q;
                if (dist <= 1f)
                {
                    float exposure = Physics.Linecast(at, SrWorld.Eye, SrWorld.SolidMask, QueryTriggerInteraction.Ignore) ? 0.3f : 1f;
                    float v = (1f - dist) * exposure;
                    Health.Damage((int)((v * v + v) / 2f * 7f * q + 1f), "explosion");
                    var push = (SrWorld.Eye - at).normalized * v * (1f - Enchants.Level(Inventory.ArmorIn("legs"), "blast_protection") * 0.15f);
                    Motor.Vel += push;
                }
            }
            foreach (var m in Mobs.Near(at, q))
            {
                float dist = Vector3.Distance(m.transform.position, at) / q;
                float v = 1f - dist;
                m.Damage((int)((v * v + v) / 2f * 7f * q + 1f), "explosion", 0, (m.transform.position - at).normalized * v);
            }
            // slimes, plorts and chickens fly off; tarrs pop
            SrWorld.ExplodeActors(at, q);
        }
    }

    /// <summary>PersistentProjectileEntity: arrows and thrown tridents.</summary>
    public class Arrow : McEntity
    {
        public ItemStack Item;
        public float Damage = 2f;
        public bool Crit, Fire, Trident, CanPickup = true, InGround;
        public int Punch, Pierce, Loyalty;
        private int inGroundTicks, returning;
        private Transform vis;
        private Vector3 stuckDir;
        private readonly HashSet<Object> pierced = new HashSet<Object>();
        private readonly HashSet<Identifiable> piercedActors = new HashSet<Identifiable>();

        public static Arrow Create(ItemStack item, Vector3 at, Vector3 dir, float speed, float divergence)
        {
            var go = new GameObject(item.Is("trident") ? "trident" : "arrow");
            go.transform.position = at;
            var a = go.AddComponent<Arrow>();
            a.Item = item;
            a.Radius = 0.05f;
            // ProjectileEntity.setVelocity: normalised direction plus a little gaussian divergence
            var d = dir.normalized + new Vector3(Gauss(), Gauss(), Gauss()) * 0.0075f * divergence;
            a.Vel = d * speed + (Motor.OnGround ? new Vector3(Motor.Vel.x, 0, Motor.Vel.z) : Motor.Vel);
            a.vis = new GameObject("vis").transform;
            a.vis.SetParent(go.transform, false);
            if (item.Is("trident"))
            {
                var v = ItemRender.Build(item, a.vis, 0, "fixed");
                v.transform.localRotation = Quaternion.Euler(90, 0, 0);
                v.transform.localPosition = Vector3.zero;
            }
            else ArrowModel(a.vis, item);
            return a;
        }

        private static float Gauss() { float u1 = 1f - Random.value, u2 = Random.value; return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Sin(2f * Mathf.PI * u2); }

        /// <summary>ArrowEntityRenderer: two crossed 16x5 quads and the back cross, from entity/projectiles/arrow.png.</summary>
        private static void ArrowModel(Transform parent, ItemStack item)
        {
            var tex = McTex.Get(item.Is("spectral_arrow") ? "entity/projectiles/spectral_arrow" : item.Is("tipped_arrow") ? "entity/projectiles/tipped_arrow" : "entity/projectiles/arrow");
            var mb = new MeshBuilder();
            Vector2 U(float u, float v) => new Vector2(u / 32f, 1f - v / 32f);
            float L = 0.5f, w = 2.5f / 16f * 0.9f;
            // two crossed shafts along +z
            for (int i = 0; i < 2; i++)
            {
                var q = Quaternion.AngleAxis(i * 90f, Vector3.forward);
                Vector3 P(float x, float y) => q * new Vector3(0, y, x);
                mb.Raw(P(-L, -w), P(-L, w), P(L, w), P(L, -w), q * Vector3.right, U(0, 5), U(0, 0), U(16, 0), U(16, 5), Color.white);
                mb.Raw(P(L, -w), P(L, w), P(-L, w), P(-L, -w), q * Vector3.left, U(16, 5), U(16, 0), U(0, 0), U(0, 5), Color.white);
            }
            var go = new GameObject("arrow mesh");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mb.Build("arrow");
            go.AddComponent<MeshRenderer>().sharedMaterial = BlockWorld.MakeMat(1, tex);
        }

        protected override void Tick()
        {
            Age++;
            if (InGround)
            {
                inGroundTicks++;
                if (Trident && Loyalty > 0 && inGroundTicks > 4) { InGround = false; returning = 1; }
                else if (inGroundTicks > 1200) { Destroy(gameObject); return; }
                TryPickup();
                return;
            }
            if (returning > 0 && SrWorld.Player != null)
            {
                // TridentEntity loyalty: flies back to the owner
                var target = SrWorld.Eye - Vector3.up * 0.3f;
                var d = target - transform.position;
                Vel = Vel * 0.95f + d.normalized * 0.05f * Loyalty;
                transform.position += Vel;
                if (returning++ == 1) Sounds.Play("item.trident.return", transform.position, 10f);
                if (d.magnitude < 1.5f) { GiveBack(); return; }
                return;
            }
            var from = transform.position;
            var to = from + Vel;
            // blocks, mobs, Slime Rancher actors and geometry along the path
            float best = Vel.magnitude;
            Mob hitMob = null; Identifiable hitLive = null; bool hitWorld = false; Vector3 hitPoint = to, hitNormal = Vector3.up;
            if (Interaction.BlockRay(from, Vel.normalized, best, out var cell, out var face, out var pt, out var bd)) { best = bd; hitWorld = true; hitPoint = pt; hitNormal = (Vector3)BlockWorld.Offset(face); }
            var m = Mobs.Raycast(from, Vel.normalized, best, out float md);
            if (m != null && !pierced.Contains(m)) { best = md; hitMob = m; hitWorld = false; hitPoint = from + Vel.normalized * md; }
            if (Physics.SphereCast(from, 0.05f, Vel.normalized, out var h, best, SrWorld.SolidMask, QueryTriggerInteraction.Ignore) && !IgnoreHit(h) && h.collider.GetComponentInParent<Mob>() == null)
            {
                var lm = SrWorld.ActorOf(h.collider);
                if (lm != null && !piercedActors.Contains(lm)) { hitLive = lm; hitMob = null; hitWorld = false; }
                else { hitWorld = true; hitMob = null; }
                hitPoint = h.point; hitNormal = h.normal;
            }
            if (hitMob != null || hitLive != null)
            {
                float speed = Vel.magnitude;
                int dmg = Mathf.CeilToInt(Mathf.Clamp(speed * Damage, 0f, int.MaxValue));
                if (Trident) dmg = Mathf.CeilToInt(8f + Item.Level("impaling") * 2.5f); // tridents hit for 8, Impaling against everything aquatic
                if (Crit && !Trident) dmg = Mathf.Min(dmg + Random.Range(0, dmg / 2 + 2), int.MaxValue);
                var push = new Vector3(Vel.x, 0, Vel.z).normalized * (Punch * 0.6f);
                if (hitMob != null) { hitMob.Damage(dmg, "arrow", Punch * 0.6f, push); if (Fire) hitMob.SetOnFire(5); pierced.Add(hitMob); }
                else
                {
                    SrWorld.HurtActor(hitLive, dmg, push + Vel.normalized * 0.3f);
                    piercedActors.Add(hitLive);
                }
                if (Item.Is("tipped_arrow") && Item.Potion != null && hitMob != null && McData.Potions.TryGetValue(Item.Potion, out var effs)) foreach (var e in effs) hitMob.AddEffect(e.Effect, e.Duration / 8, e.Amplifier);
                Sounds.Play(Trident ? "item.trident.hit" : "entity.arrow.hit", hitPoint);
                if (Trident) { Vel = -Vel * 0.01f; if (Loyalty > 0) returning = 1; transform.position = hitPoint; return; }
                if (Pierce-- > 0) { transform.position = to; return; }
                Destroy(gameObject);
                return;
            }
            if (hitWorld)
            {
                transform.position = hitPoint - Vel.normalized * 0.05f;
                stuckDir = Vel.normalized;
                Vel = Vector3.zero;
                InGround = true;
                Sounds.Play(Trident ? "item.trident.hit_ground" : "entity.arrow.hit", hitPoint, 1f, 1.2f / (Random.value * 0.2f + 0.9f));
                Crit = false;
                return;
            }
            transform.position = to;
            InWater = SrWorld.IsWater(transform.position);
            // drag and gravity (0.99 in air, 0.6 in water; the trident keeps 0.99 underwater)
            float drag = InWater ? (Trident ? 0.99f : 0.6f) : 0.99f;
            Vel *= drag;
            Vel.y -= 0.05f;
            if (Crit && Age % 2 == 0) Particles.Crit(transform.position);
        }

        private void TryPickup()
        {
            if (SrWorld.Player == null) return;
            var d = SrWorld.Eye - Vector3.up * 0.9f - transform.position;
            if (d.magnitude > 1.6f) return;
            GiveBack();
        }

        private void GiveBack()
        {
            if (CanPickup || Trident && !GameMode.Creative) { if (Inventory.Add(Item.Copy(1)) == 0) return; }
            Sounds.Play("entity.item.pickup", transform.position, 0.2f, ((Random.value - Random.value) * 0.7f + 1f) * 2f);
            Destroy(gameObject);
        }

        protected override void Render(float partial)
        {
            var v = InGround ? stuckDir : Vel;
            if (v.sqrMagnitude > 1e-6f) vis.rotation = Quaternion.LookRotation(v);
        }
    }

    /// <summary>ThrownItemEntity: snowballs, eggs, ender pearls, potions, bottles o' enchanting.</summary>
    public class Thrown : McEntity
    {
        public ItemStack Item;
        private Transform vis;

        public static Thrown Create(ItemStack item, Vector3 at, Vector3 dir, float speed)
        {
            var go = new GameObject("thrown " + item.Id);
            go.transform.position = at;
            var t = go.AddComponent<Thrown>();
            t.Item = item;
            t.Radius = 0.12f;
            t.Vel = dir.normalized * speed + Motor.Vel;
            var holder = new GameObject("vis").transform;
            holder.SetParent(go.transform, false);
            ItemRender.Build(item, holder, 0, "ground");
            holder.localScale = Vector3.one * 2f;
            t.vis = holder;
            return t;
        }

        protected override void Tick()
        {
            Age++;
            var from = transform.position;
            float len = Vel.magnitude;
            bool hit = false; Vector3 point = from + Vel; Mob mob = null; Identifiable live = null;
            if (Interaction.BlockRay(from, Vel / Mathf.Max(len, 1e-5f), len, out _, out _, out var pt, out var d)) { hit = true; point = pt; len = d; }
            var m = Mobs.Raycast(from, Vel / Mathf.Max(len, 1e-5f), len, out float md);
            if (m != null) { hit = true; mob = m; point = from + Vel.normalized * md; len = md; }
            if (Physics.SphereCast(from, 0.1f, Vel.normalized, out var h, len, SrWorld.SolidMask, QueryTriggerInteraction.Ignore) && !IgnoreHit(h))
            {
                hit = true; point = h.point; mob = null;
                live = SrWorld.ActorOf(h.collider);
            }
            if (hit) { Impact(point, mob, live); Destroy(gameObject); return; }
            transform.position += Vel;
            InWater = SrWorld.IsWater(transform.position);
            Vel *= InWater ? 0.8f : 0.99f;
            Vel.y -= Item.Is("experience_bottle") ? 0.07f : Item.Item.Id.EndsWith("potion") ? 0.05f : 0.03f;
            if (Age > 400) Destroy(gameObject);
        }

        private void Impact(Vector3 at, Mob mob, Identifiable live)
        {
            var id = Item.Item.Id;
            switch (id)
            {
                case "snowball":
                    if (mob != null) mob.Damage(mob.Type == "blaze" ? 3 : 0, "thrown", 0.4f, Vel.normalized);
                    Particles.ItemCrack(at, Item, 8);
                    break;
                case "egg":
                    if (mob != null) mob.Damage(0, "thrown", 0.4f, Vel.normalized);
                    if (Random.Range(0, 8) == 0) Mobs.Summon("chicken", at);
                    Particles.ItemCrack(at, Item, 8);
                    break;
                case "ender_pearl":
                    // teleports the player and costs 5 health
                    if (SrWorld.Player != null)
                    {
                        SrWorld.Teleport(at);
                        Motor.FallDistance = 0;
                        Health.Damage(5f, "fall");
                        Sounds.Play("entity.player.teleport", at);
                    }
                    break;
                case "experience_bottle":
                    Sounds.Play("entity.splash_potion.break", at);
                    Particles.Splash(at, 0x385DC6);
                    XpOrb.Spawn(at, 3 + Random.Range(0, 5) + Random.Range(0, 5));
                    break;
                default:
                    // splash and lingering potions: everything within 4 blocks, scaled by distance
                    Sounds.Play("entity.splash_potion.break", at);
                    Particles.Splash(at, ItemRender.PotionColor(Item.Potion));
                    if (Item.Potion != null && McData.Potions.TryGetValue(Item.Potion, out var effs))
                    {
                        if (SrWorld.Player != null)
                        {
                            float dist = Vector3.Distance(SrWorld.Feet + Vector3.up * 0.6f, at);
                            if (dist < 4f)
                            {
                                float k = 1f - dist / 4f;
                                foreach (var e in effs)
                                {
                                    if (McData.Effects[e.Effect].Instant) { if (e.Effect == "instant_health") Health.Heal((4 << e.Amplifier) * k); else if (e.Effect == "instant_damage") Health.Damage((6 << e.Amplifier) * k, "magic"); }
                                    else Effects.Add(e.Effect, (int)(e.Duration * k), e.Amplifier);
                                }
                            }
                        }
                        foreach (var mb in Mobs.Near(at, 4f)) foreach (var e in effs) mb.AddEffect(e.Effect, e.Duration, e.Amplifier);
                        foreach (var c in Physics.OverlapSphere(at, 4f, ~0, QueryTriggerInteraction.Ignore))
                        {
                            var lm = SrWorld.ActorOf(c);
                            if (lm == null) continue;
                            foreach (var e in effs)
                            {
                                if (e.Effect == "instant_damage") SrWorld.HurtActor(lm, 6 << e.Amplifier, Vector3.zero);
                                if (e.Effect == "poison") SrWorld.HurtActor(lm, 4, Vector3.zero);
                            }
                        }
                    }
                    break;
            }
            if (live != null && (id == "snowball" || id == "egg")) SrWorld.HurtActor(live, 0f, Vel.normalized * 0.4f);
        }

        protected override void Render(float partial)
        {
            if (vis != null && Camera.main != null) vis.rotation = Camera.main.transform.rotation;
        }
    }

    /// <summary>FireworkRocketEntity flying on its own: accelerates upward, sparkles, bursts at the end of its life.</summary>
    public class FireworkEntity : McEntity
    {
        public int Life;
        public bool Straight;
        private Transform vis;

        protected override void Start()
        {
            base.Start();
            vis = new GameObject("vis").transform;
            vis.SetParent(transform, false);
            ItemRender.Build(ItemStack.Of("firework_rocket"), vis, 0, "ground");
        }

        protected override void Tick()
        {
            Age++;
            if (!Straight) { Vel.x *= 1.15f; Vel.z *= 1.15f; Vel.y += 0.04f; }
            transform.position += Vel;
            if (Age % 2 == 0) Particles.Rocket(transform.position);
            if (Age > Life || Interaction.BlockRay(transform.position, Vel.normalized, Vel.magnitude, out _, out _, out _, out _))
            {
                Sounds.Play("entity.firework_rocket.blast", transform.position, 3f);
                Particles.Firework(transform.position);
                Destroy(gameObject);
            }
        }

        protected override void Render(float partial)
        {
            if (vis != null && Vel.sqrMagnitude > 0) vis.rotation = Quaternion.LookRotation(Vel) * Quaternion.Euler(90, 0, 0);
        }
    }

    /// <summary>TntEntity: 80 ticks of fuse, flashing white, then a power 4 explosion.</summary>
    public class TntEntity : McEntity
    {
        private int fuse = 80;
        private Renderer r;

        protected override void Start()
        {
            base.Start();
            var mb = new MeshBuilder(1);
            var s = McData.Block("tnt").Default;
            foreach (var mr in McData.ModelsFor(s)) foreach (var q in ModelBaker.Bake(mr)) mb.Add(q, -Vector3.one * 0.5f);
            var go = new GameObject("vis");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mb.Build("tnt");
            r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = BlockWorld.Materials[0];
        }

        protected override void Tick()
        {
            Vel.y -= 0.04f;
            MoveCollide();
            Vel *= 0.98f;
            if (OnGround) { Vel.x *= 0.7f; Vel.z *= 0.7f; Vel.y *= -0.5f; }
            if (--fuse <= 0) { Projectiles.Explode(transform.position, 4f); Destroy(gameObject); return; }
            if (fuse / 5 % 2 == 0 && r != null) r.material.color = new Color(2, 2, 2); else if (r != null) r.material.color = Color.white;
            if (Age++ % 3 == 0) Particles.Smoke(transform.position + Vector3.up * 0.5f);
        }

        protected override void Render(float partial) => transform.position = Vector3.Lerp(prevPos, curPos, partial);
    }

    /// <summary>The Riptide spin: the player keeps the launch and hurts what they fly into.</summary>
    public static class Riptide
    {
        public static int Ticks;
        public static void Start(int t) { Ticks = t; }
        public static void Tick()
        {
            if (Ticks <= 0) return;
            Ticks--;
            if (SrWorld.Player == null) return;
            var eye = SrWorld.Eye;
            foreach (var m in Mobs.Near(eye, 1.5f)) m.Damage(8f, "player", 0.4f, Motor.Vel.normalized);
            foreach (var c in Physics.OverlapSphere(eye, 1.5f, ~0, QueryTriggerInteraction.Ignore))
            {
                var lm = SrWorld.ActorOf(c);
                if (lm != null) { SrWorld.HurtActor(lm, 8f, Motor.Vel.normalized * 0.4f); Ticks = 0; Motor.Vel *= -0.2f; break; }
            }
        }
    }
}
