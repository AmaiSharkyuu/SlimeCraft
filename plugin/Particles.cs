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

        /// <summary>
        /// One unlit material per texture. Slime Rancher's Paintlight shader ignored the texture region and the colour
        /// of each particle (every particle showed the whole atlas), so the quads carry their uv and colour themselves.
        /// </summary>
        private static Material Mat(Texture t)
        {
            if (t == null) t = Texture2D.whiteTexture;
            if (mats.TryGetValue(t, out var m) && m != null) return m;
            m = new Material(BlockWorld.FindShader("Sprites/Default")) { name = "mc:particles", mainTexture = t, renderQueue = 3100 };
            Object.DontDestroyOnLoad(m);
            mats[t] = m;
            return m;
        }

        private static readonly Dictionary<Texture, Mesh> meshes = new Dictionary<Texture, Mesh>();
        private static readonly List<Vector3> mv = new List<Vector3>();
        private static readonly List<Vector2> muv = new List<Vector2>();
        private static readonly List<Color> mcol = new List<Color>();
        private static readonly List<int> mtri = new List<int>();

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
            var look = cam.forward;
            if (View.I != null && View.I.Perspective != 0)
            {
                // F5: the camera is away from the head, the bits come out of the mouth
                var head = SrWorld.Player.transform.rotation * Vector3.forward;
                if (View.I.Perspective == 2) head = -cam.forward;
                look = new Vector3(head.x, 0, head.z).normalized;
            }
            var at = SrWorld.Eye + look * 0.35f - Vector3.up * 0.15f;
            Texture tex; Rect uv; Color tint = Color.white; bool goo = false;
            if (s.IsSr && Identifiable.IsSlime(s.Actor) && Atlas.Has("item/slime_ball"))
            {
                tex = Atlas.Texture; uv = Atlas.Rect("item/slime_ball"); tint = SrItems.Color(s.Actor); tint.a = 1f; goo = true;
            }
            else tex = ItemRender.Icon(s, out uv);
            for (int i = 0; i < (goo ? 8 : 5); i++)
            {
                // the middle of the picture: Slime Rancher icons have empty borders
                var piece = s.IsSr && !goo ? Sub(uv, Random.Range(1, 3), Random.Range(1, 3), 4) : Sub(uv, Random.Range(0, 4), Random.Range(0, 4), 4);
                Add(new P { Pos = at + Random.insideUnitSphere * 0.08f, Vel = look * 0.06f + Random.insideUnitSphere * 0.05f + Vector3.up * 0.08f, Size = goo ? 0.05f : 0.04f, Life = 12 + Random.Range(0, 6), Tex = tex, Uv = piece, Color = tint });
            }
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

        /// <summary>A note block's note, coloured round the colour wheel by pitch like Minecraft's.</summary>
        public static void Note(Vector3 at, int note)
        {
            float t = note / 24f;
            var c = new Color(Mathf.Max(0f, Mathf.Sin((t + 0f) * Mathf.PI * 2f) * 0.65f + 0.35f), Mathf.Max(0f, Mathf.Sin((t + 1f / 3f) * Mathf.PI * 2f) * 0.65f + 0.35f), Mathf.Max(0f, Mathf.Sin((t + 2f / 3f) * Mathf.PI * 2f) * 0.65f + 0.35f));
            Sheet(at, Vector3.up * 0.02f, "note", 0.2f, 6, c);
        }

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
            // one mesh per texture, camera-facing quads with their own uv and colour
            var cam = Camera.main.transform;
            Vector3 right = cam.right, up = cam.up;
            float pt = acc / 0.05f;
            float light = Mathf.Clamp(RenderSettings.ambientLight.grayscale * 1.6f + 0.35f, 0.35f, 1f);   // darker at night, like Minecraft's lit particles
            var groups = new Dictionary<Texture, List<P>>();
            foreach (var p in live)
            {
                var t = p.Tex ?? Texture2D.whiteTexture;
                if (!groups.TryGetValue(t, out var l)) groups[t] = l = new List<P>();
                l.Add(p);
            }
            foreach (var kv in meshes) if (kv.Value != null && !groups.ContainsKey(kv.Key)) kv.Value.Clear();
            foreach (var g in groups)
            {
                mv.Clear(); muv.Clear(); mcol.Clear(); mtri.Clear();
                foreach (var p in g.Value)
                {
                    var c = p.Pos + p.Vel * pt;
                    var r = right * p.Size; var u = up * p.Size;
                    int b = mv.Count;
                    mv.Add(c - r - u); mv.Add(c - r + u); mv.Add(c + r + u); mv.Add(c + r - u);
                    muv.Add(new Vector2(p.Uv.xMin, p.Uv.yMin)); muv.Add(new Vector2(p.Uv.xMin, p.Uv.yMax)); muv.Add(new Vector2(p.Uv.xMax, p.Uv.yMax)); muv.Add(new Vector2(p.Uv.xMax, p.Uv.yMin));
                    var col = p.Glow ? p.Color : new Color(p.Color.r * light, p.Color.g * light, p.Color.b * light, p.Color.a);
                    mcol.Add(col); mcol.Add(col); mcol.Add(col); mcol.Add(col);
                    mtri.Add(b); mtri.Add(b + 1); mtri.Add(b + 2); mtri.Add(b); mtri.Add(b + 2); mtri.Add(b + 3);
                }
                if (!meshes.TryGetValue(g.Key, out var m) || m == null)
                {
                    m = new Mesh { name = "mc particles" };
                    m.MarkDynamic();
                    Object.DontDestroyOnLoad(m);
                    meshes[g.Key] = m;
                }
                m.Clear();
                m.SetVertices(mv); m.SetUVs(0, muv); m.SetColors(mcol); m.SetTriangles(mtri, 0);
                m.bounds = new Bounds(cam.position, Vector3.one * 2000f);
                Graphics.DrawMesh(m, Matrix4x4.identity, Mat(g.Key), 0);
            }
        }
    }

    /// <summary>Teleports used by chorus fruit and ender pearls.</summary>
    public static class Teleport
    {
        public static void Random(float range)
        {
            for (int i = 0; i < 16; i++)
            {
                var feet = SrWorld.Feet;
                var to = feet + new Vector3(UnityEngine.Random.Range(-range, range), UnityEngine.Random.Range(-range, range), UnityEngine.Random.Range(-range, range));
                if (Physics.CheckCapsule(to + Vector3.up * 0.31f, to + Vector3.up * 1.49f, 0.3f, SrWorld.SolidMask, QueryTriggerInteraction.Ignore)) continue;
                if (!Physics.Raycast(to + Vector3.up * 0.1f, Vector3.down, 3f, SrWorld.SolidMask, QueryTriggerInteraction.Ignore) && !SrWorld.IsWater(to)) continue;
                SrWorld.Teleport(to);
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
                vel.y += (SrWorld.WaterLevelAt(p) - p.y > 0.2f ? 0.04f : 0f) - 0.01f;
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
                if (Random.value < 0.08f)
                {
                    // now and then a puddle plort, washed into Slime Rancher's ponds
                    catchStack = new ItemStack(Identifiable.Id.PUDDLE_PLORT, 1);
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
