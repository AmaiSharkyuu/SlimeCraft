using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Minecraft's particles as camera-facing quads: block break / hit fragments (BlockDustParticle with a random
    /// 4x4 bit of the block's particle texture), item crack (eating, thrown items), crits, enchanted hits, the sweep,
    /// smoke, explosions, firework sparks and splashes. Simple CPU particles with Minecraft's gravity and lifetimes.
    /// </summary>
    public static class Particles
    {
        private class P
        {
            public Vector3 Pos, Vel;
            public float Size, Gravity = 0.04f, Drag = 0.98f;
            public int Age, Life;
            public Texture Tex; public Rect Uv;
            public Color Color = Color.white;
            public bool Collide = true, Glow;
            public int Frames; public string Sheet;
        }

        private static readonly List<P> live = new List<P>();
        private static GameObject host;
        private static Mesh mesh;
        private static readonly Dictionary<Texture, Material> mats = new Dictionary<Texture, Material>();
        private static float acc;
        private static MaterialPropertyBlock block;

        private static Material Mat(Texture t, bool glow)
        {
            if (t == null) t = Texture2D.whiteTexture;
            if (mats.TryGetValue(t, out var m) && m != null) return m;
            m = BlockWorld.MakeMat(1, t);
            m.enableInstancing = true;
            m.SetFloat("_Cutoff", 0.1f);
            if (glow) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", Color.white * 0.8f); m.SetTexture("_EmissionMap", t); }
            mats[t] = m;
            return m;
        }

        private static void Add(P p)
        {
            if (live.Count > 1500) live.RemoveAt(0);
            live.Add(p);
        }

        private static Rect Sub(Rect r, int x, int y, int n) => new Rect(r.x + r.width * x / n, r.y + r.height * y / n, r.width / n, r.height / n);

        private static string ParticleTex(BState s)
        {
            var models = McData.ModelsFor(s);
            if (models.Count > 0 && McData.Models.TryGetValue(models[0].Model, out var m) && m["particle"] != null) return (string)m["particle"];
            return "block/stone";
        }

        /// <summary>BlockDustParticle burst when a block breaks: 4x4x4 fragments.</summary>
        public static void Break(Vector3 center, BState s)
        {
            if (s == null) return;
            var tex = ParticleTex(s);
            var r = Atlas.Rect(s.Block.Tint >= 0 && Atlas.Has(Atlas.Tinted(tex, s.Block.Tint)) ? Atlas.Tinted(tex, s.Block.Tint) : tex);
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    for (int k = 0; k < 4; k++)
                    {
                        var off = new Vector3((i + 0.5f) / 4f, (j + 0.5f) / 4f, (k + 0.5f) / 4f) - Vector3.one * 0.5f;
                        var v = off * 2f + Random.insideUnitSphere * 0.4f;
                        Add(new P
                        {
                            Pos = center + off, Vel = v * 0.1f + Vector3.up * 0.1f, Size = 0.1f * (Random.value * 0.5f + 0.5f),
                            Life = (int)(4f / (Random.value * 0.9f + 0.1f)), Tex = Atlas.Texture, Uv = Sub(r, Random.Range(0, 4), Random.Range(0, 4), 4),
                        });
                    }
        }

        /// <summary>The fragments flying off the face being mined (ParticleManager.addBlockBreakingParticles).</summary>
        public static void Hit(Vector3 point, Vector3 normal, BState s)
        {
            if (s == null || Random.value > 0.6f) return;
            var r = Atlas.Rect(ParticleTex(s));
            Add(new P
            {
                Pos = point + normal * 0.1f + Random.insideUnitSphere * 0.2f, Vel = (normal * 0.2f + Random.insideUnitSphere * 0.1f) * 0.6f,
                Size = 0.1f * (Random.value * 0.5f + 0.5f) * 0.6f, Life = 8 + Random.Range(0, 8), Tex = Atlas.Texture, Uv = Sub(r, Random.Range(0, 4), Random.Range(0, 4), 4),
            });
        }

        public static void BlockDust(Vector3 feet, BState s, int count)
        {
            if (s == null) return;
            var r = Atlas.Rect(ParticleTex(s));
            for (int i = 0; i < count; i++)
                Add(new P { Pos = feet + new Vector3(Random.Range(-0.4f, 0.4f), 0.05f, Random.Range(-0.4f, 0.4f)), Vel = new Vector3(Random.Range(-0.1f, 0.1f), 0.15f, Random.Range(-0.1f, 0.1f)), Size = 0.08f, Life = 10 + Random.Range(0, 10), Tex = Atlas.Texture, Uv = Sub(r, Random.Range(0, 4), Random.Range(0, 4), 4) });
        }

        public static void ItemCrack(Vector3 at, ItemStack s, int n)
        {
            var tex = ItemRender.Icon(s, out var uv);
            for (int i = 0; i < n; i++)
                Add(new P { Pos = at, Vel = Random.insideUnitSphere * 0.15f + Vector3.up * 0.1f, Size = 0.08f, Life = 10 + Random.Range(0, 10), Tex = tex, Uv = Sub(uv, Random.Range(0, 4), Random.Range(0, 4), 4) });
        }

        /// <summary>Eating: bits of the food in front of the mouth (LivingEntity.spawnConsumptionEffects).</summary>
        public static void Eat(ItemStack s)
        {
            var cam = Camera.main.transform;
            var at = cam.position + cam.forward * 0.6f - cam.up * 0.3f;
            var tex = ItemRender.Icon(s, out var uv);
            for (int i = 0; i < 5; i++)
                Add(new P { Pos = at + Random.insideUnitSphere * 0.1f, Vel = cam.forward * 0.05f + Random.insideUnitSphere * 0.05f + Vector3.up * 0.05f, Size = 0.04f, Life = 10, Tex = tex, Uv = Sub(uv, Random.Range(0, 4), Random.Range(0, 4), 4) });
        }

        public static void ItemBreak(ItemStack s)
        {
            if (SrWorld.Player == null || s == null) return;
            var cam = Camera.main.transform;
            ItemCrack(cam.position + cam.forward * 0.6f - cam.up * 0.3f, s, 5);
        }

        private static void Sheet(Vector3 at, Vector3 vel, string tex, float size, int life, Color c, float gravity = 0f, bool glow = false)
        {
            Add(new P { Pos = at, Vel = vel, Size = size, Life = life, Tex = McTex.Get("particle/" + tex), Uv = new Rect(0, 0, 1, 1), Color = c, Gravity = gravity, Collide = false, Glow = glow });
        }

        public static void Crit(Vector3 at)
        {
            for (int i = 0; i < 12; i++) Sheet(at + Random.insideUnitSphere * 0.3f, Random.insideUnitSphere * 0.15f + Vector3.up * 0.1f, "critical_hit", 0.08f, 12, new Color(0.9f, 0.9f, 0.9f), 0.03f);
        }

        public static void EnchantedHit(Vector3 at)
        {
            for (int i = 0; i < 12; i++) Sheet(at + Random.insideUnitSphere * 0.3f, Random.insideUnitSphere * 0.15f + Vector3.up * 0.1f, "enchanted_hit", 0.08f, 12, new Color(0.6f, 0.9f, 1f), 0.03f);
        }

        public static void Sweep(Vector3 at, Vector3 dir)
        {
            Add(new P { Pos = at + Vector3.up * 0.2f, Vel = Vector3.zero, Size = 0.6f, Life = 4, Tex = McTex.Get("particle/sweep_0"), Uv = new Rect(0, 0, 1, 1), Collide = false, Gravity = 0, Frames = 8, Sheet = "particle/sweep_" });
        }

        public static void Smoke(Vector3 at) => Sheet(at, Vector3.up * 0.05f + Random.insideUnitSphere * 0.02f, "generic_" + Random.Range(0, 8), 0.12f, 20, new Color(0.3f, 0.3f, 0.3f));

        public static void Explosion(Vector3 at, float power)
        {
            for (int i = 0; i < 16; i++)
                Add(new P { Pos = at + Random.insideUnitSphere * power * 0.5f, Vel = Vector3.zero, Size = power * 0.5f, Life = 8 + Random.Range(0, 4), Tex = McTex.Get("particle/explosion_0"), Uv = new Rect(0, 0, 1, 1), Collide = false, Gravity = 0, Frames = 16, Sheet = "particle/explosion_", Glow = true });
        }

        public static void Rocket(Vector3 at) => Sheet(at, Random.insideUnitSphere * 0.03f - Vector3.up * 0.05f, "spark_" + Random.Range(0, 8), 0.1f, 12, Color.white, 0f, true);

        public static void Firework(Vector3 at)
        {
            var c = Color.HSVToRGB(Random.value, 0.8f, 1f);
            for (int i = 0; i < 80; i++) Sheet(at, Random.onUnitSphere * 0.4f, "spark_" + Random.Range(0, 8), 0.12f, 30 + Random.Range(0, 10), c, 0.004f, true);
        }

        public static void Splash(Vector3 at, int color)
        {
            var c = ItemRender.Hex(color);
            for (int i = 0; i < 40; i++) Sheet(at, Random.onUnitSphere * 0.2f + Vector3.up * 0.1f, "effect_" + Random.Range(0, 8), 0.1f, 20, c, 0.01f);
        }

        public static void Bubble(Vector3 at) => Sheet(at, Vector3.up * 0.05f + Random.insideUnitSphere * 0.01f, "bubble", 0.06f, 20 + Random.Range(0, 20), Color.white);

        public static void Heart(Vector3 at) => Sheet(at, Vector3.up * 0.04f, "heart", 0.15f, 20, Color.white);

        public static void Angry(Vector3 at) => Sheet(at, Vector3.up * 0.02f, "angry", 0.15f, 20, Color.white);

        public static void Clear() => live.Clear();

        public static void Update()
        {
            if (live.Count == 0 || Camera.main == null) return;
            acc += Time.deltaTime;
            while (acc >= 0.05f)
            {
                acc -= 0.05f;
                for (int i = live.Count - 1; i >= 0; i--)
                {
                    var p = live[i];
                    if (++p.Age >= p.Life) { live.RemoveAt(i); continue; }
                    bool water = SrWorld.IsWater(p.Pos);
                    p.Vel.y -= water ? p.Gravity * 0.2f : p.Gravity;
                    var next = p.Pos + p.Vel;
                    if (p.Collide && (BlockWorld.Has(BlockWorld.CellAt(next)) && BlockWorld.Get(BlockWorld.CellAt(next)).FullCube || Physics.Linecast(p.Pos, next, SrWorld.SolidMask, QueryTriggerInteraction.Ignore)))
                    { p.Vel = new Vector3(p.Vel.x * 0.7f, 0, p.Vel.z * 0.7f); next = p.Pos; }
                    p.Pos = next;
                    p.Vel *= water ? p.Drag * 0.9f : p.Drag;
                    if (p.Sheet != null && p.Frames > 0) p.Tex = McTex.Get(p.Sheet + Mathf.Min(p.Frames - 1, p.Age * p.Frames / Mathf.Max(1, p.Life)));
                }
            }
            if (mesh == null)
            {
                mesh = new Mesh();
                mesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(0.5f, -0.5f, 0) };
                mesh.uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
                mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
                mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                Object.DontDestroyOnLoad(mesh);
            }
            if (block == null) block = new MaterialPropertyBlock();
            var rot = Camera.main.transform.rotation;
            float pt = acc / 0.05f;
            foreach (var p in live)
            {
                block.Clear();
                block.SetVector("_MainTex_ST", new Vector4(p.Uv.width, p.Uv.height, p.Uv.x, p.Uv.y));
                block.SetColor("_Color", p.Color);
                var m = Matrix4x4.TRS(p.Pos + p.Vel * pt, rot, Vector3.one * p.Size * 2f);
                Graphics.DrawMesh(mesh, m, Mat(p.Tex, p.Glow), 0, null, 0, block, false, false);
            }
        }
    }

    /// <summary>Teleports used by chorus fruit and ender pearls.</summary>
    public static class Teleport
    {
        public static void Random(Player p, float range)
        {
            for (int i = 0; i < 16; i++)
            {
                var feet = p.transform.position - Vector3.up * Motor.EyeHeight;
                var to = feet + new Vector3(UnityEngine.Random.Range(-range, range), UnityEngine.Random.Range(-range, range), UnityEngine.Random.Range(-range, range));
                if (Physics.CheckCapsule(to + Vector3.up * 0.31f, to + Vector3.up * 1.49f, 0.3f, SrWorld.SolidMask, QueryTriggerInteraction.Ignore)) continue;
                if (!Physics.Raycast(to + Vector3.up * 0.1f, Vector3.down, 3f, SrWorld.SolidMask, QueryTriggerInteraction.Ignore) && !SrWorld.IsWater(to)) continue;
                p.SetPosition(to + Vector3.up * Motor.EyeHeight);
                Sounds.Play("item.chorus_fruit.teleport", to);
                return;
            }
        }
    }

    /// <summary>
    /// Fishing rod: casts a bobber that sinks or floats, bites after Minecraft's wait (shorter with Lure), and reels
    /// in Minecraft's fishing loot (cod, salmon, tropical fish, pufferfish, junk and treasure with Luck of the Sea)
    /// or, half the time out in Subnautica's ocean, a Subnautica fish of the area.
    /// </summary>
    public static class Fishing
    {
        private static GameObject bobber;
        private static Vector3 vel;
        private static int wait, biteTicks;
        private static ItemStack rod;
        private static LineRenderer line;

        public static bool Out => bobber != null;

        public static void Toggle(ItemStack r)
        {
            if (bobber != null) { Reel(); return; }
            rod = r;
            var cam = Camera.main.transform;
            bobber = new GameObject("mc bobber");
            bobber.transform.position = cam.position + cam.forward * 0.5f;
            var vis = new GameObject("vis");
            vis.transform.SetParent(bobber.transform, false);
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Object.Destroy(q.GetComponent<Collider>());
            q.transform.SetParent(vis.transform, false);
            q.transform.localScale = Vector3.one * 0.25f;
            var mat = BlockWorld.MakeMat(1, McTex.Get("entity/fishing_hook"));
            q.GetComponent<MeshRenderer>().sharedMaterial = mat;
            line = bobber.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.startWidth = line.endWidth = 0.01f;
            line.material = BlockWorld.MakeMat(0, Texture2D.whiteTexture);
            line.material.color = Color.black;
            vel = cam.forward * 0.6f + Vector3.up * 0.1f;
            wait = Random.Range(100, 600) - rod.Level("lure") * 100;
            biteTicks = 0;
            Sounds.Play("entity.fishing_bobber.throw", cam.position, 0.5f, 0.4f / (Random.value * 0.4f + 0.8f));
        }

        public static void Tick()
        {
            if (bobber == null) return;
            if (Inventory.Held != rod || SrWorld.Player == null || (bobber.transform.position - SrWorld.Eye).sqrMagnitude > 32 * 32) { Remove(); return; }
            var p = bobber.transform.position;
            bool water = SrWorld.IsWater(p);
            if (water)
            {
                vel *= 0.9f;
                vel.y += (SrWorld.WaterLevelAt(transform.position) - p.y > 0.2f ? 0.04f : 0f) - 0.01f;
                if (wait > 0) wait--;
                else if (biteTicks == 0) { biteTicks = Random.Range(20, 40); Sounds.Play("entity.fishing_bobber.splash", p, 0.25f); Particles.Bubble(p); vel.y -= 0.2f; }
                else if (--biteTicks == 0) wait = Random.Range(100, 600) - rod.Level("lure") * 100;
            }
            else
            {
                vel.y -= 0.03f;
                vel *= 0.92f;
                if (Physics.Linecast(p, p + vel, SrWorld.SolidMask, QueryTriggerInteraction.Ignore) || BlockWorld.Has(BlockWorld.CellAt(p + vel))) vel = Vector3.zero;
            }
            bobber.transform.position = p + vel;
            if (line != null) { line.SetPosition(0, Camera.main.transform.position + Camera.main.transform.right * 0.3f - Camera.main.transform.up * 0.2f); line.SetPosition(1, bobber.transform.position); }
            if (Camera.main != null) bobber.transform.GetChild(0).rotation = Camera.main.transform.rotation;
        }

        private static void Reel()
        {
            var at = bobber.transform.position;
            if (biteTicks > 0 && SrWorld.IsWater(at))
            {
                ItemStack catchStack;
                if (Random.value < 0.5f)
                {
                    Identifiable.Id[] fish = { Identifiable.Id.Peeper, Identifiable.Id.Bladderfish, Identifiable.Id.Boomerang, Identifiable.Id.GarryFish, Identifiable.Id.HoleFish, Identifiable.Id.Hoopfish, Identifiable.Id.Reginald, Identifiable.Id.Spadefish, Identifiable.Id.Eyeye };
                    catchStack = new ItemStack(fish[Random.Range(0, fish.Length)], 1);
                }
                else
                {
                    var table = McData.EntityLoot.ContainsKey("fishing") ? McData.EntityLoot["fishing"] : null;
                    var drops = Loot.Roll(table, new Loot.Ctx { Tool = rod });
                    catchStack = drops.Count > 0 ? drops[0] : ItemStack.Of(Random.value < 0.6f ? "cod" : "salmon");
                }
                var toPlayer = (SrWorld.Eye - at);
                var e = ItemEntity.Spawn(catchStack, at, new Vector3(toPlayer.x * 0.1f, toPlayer.y * 0.1f + Mathf.Sqrt(toPlayer.magnitude) * 0.08f, toPlayer.z * 0.1f), 0f);
                XpOrb.Spawn(SrWorld.Eye - Vector3.up, Random.Range(1, 7));
                Inventory.DamageHeld(1);
            }
            Sounds.Play("entity.fishing_bobber.retrieve", at, 1f, 0.4f / (Random.value * 0.4f + 0.8f));
            Remove();
        }

        private static void Remove()
        {
            if (bobber != null) Object.Destroy(bobber);
            bobber = null;
        }
    }

    /// <summary>Bone meal: grows saplings into Minecraft trees (simple oak / birch / spruce shapes), crops, grass and flowers.</summary>
    public static class BoneMeal
    {
        public static bool Apply(Vector3Int c)
        {
            var s = BlockWorld.Get(c);
            if (s == null) return false;
            Particles.Hit(BlockWorld.Center(c), Vector3.up, s);
            Sounds.Play("item.bone_meal.use", BlockWorld.Center(c));
            var cls = s.Block.Class;
            if (cls == "SaplingBlock")
            {
                if (Random.value < 0.45f) Tree(c, s.Block.Id.Replace("_sapling", ""));
                return true;
            }
            if (cls == "CropBlock" || cls == "CarrotsBlock" || cls == "PotatoesBlock" || cls == "BeetrootsBlock")
            {
                var vals = BlockRules.PropValues(s.Block, "age");
                int age = int.Parse(s["age"] ?? "0");
                int max = vals.Length - 1;
                BlockWorld.Set(c, s.With("age", Mathf.Min(max, age + Random.Range(2, 6)).ToString()));
                return true;
            }
            if (s.Block.Id == "grass_block")
            {
                for (int i = 0; i < 24; i++)
                {
                    var p = c + new Vector3Int(Random.Range(-3, 4), 1, Random.Range(-3, 4));
                    var below = BlockWorld.Get(p + Vector3Int.down);
                    if (BlockWorld.Has(p) || below == null || below.Block.Id != "grass_block") continue;
                    var flower = Random.value < 0.8f ? "grass" : Random.value < 0.5f ? "dandelion" : "poppy";
                    BlockWorld.Set(p, McData.Block(flower).Default, false);
                }
                return true;
            }
            return false;
        }

        /// <summary>A tree like Minecraft's straight trunk placer with a blob of leaves.</summary>
        public static void Tree(Vector3Int c, string wood)
        {
            var log = McData.Block(wood + "_log") ?? McData.Block("oak_log");
            var leaves = McData.Block(wood + "_leaves") ?? McData.Block("oak_leaves");
            int h = wood == "spruce" ? Random.Range(6, 9) : Random.Range(4, 7);
            for (int y = 1; y < h + 2; y++) if (BlockWorld.Has(c + Vector3Int.up * y)) return;
            BlockWorld.Remove(c, false);
            for (int y = 0; y < h; y++) BlockWorld.Set(c + Vector3Int.up * y, log.Default, false);
            var ls = leaves.Default.With("persistent", "false");
            if (wood == "spruce")
            {
                for (int y = 2; y <= h; y++)
                {
                    int r = (h - y) % 2 == 0 ? 2 : 1;
                    if (y == h) r = 0;
                    for (int x = -r; x <= r; x++) for (int z = -r; z <= r; z++)
                        if (Mathf.Abs(x) + Mathf.Abs(z) <= r + 1 && !BlockWorld.Has(c + new Vector3Int(x, y, z))) BlockWorld.Set(c + new Vector3Int(x, y, z), ls, false);
                }
                BlockWorld.Set(c + Vector3Int.up * (h + 1), ls, false);
            }
            else
            {
                for (int y = h - 3; y <= h; y++)
                {
                    int r = y >= h - 1 ? 1 : 2;
                    for (int x = -r; x <= r; x++) for (int z = -r; z <= r; z++)
                    {
                        if (Mathf.Abs(x) == r && Mathf.Abs(z) == r && (y == h || Random.value < 0.5f)) continue;
                        var p = c + new Vector3Int(x, y, z);
                        if (!BlockWorld.Has(p)) BlockWorld.Set(p, ls, false);
                    }
                }
            }
        }
    }
}
