using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>Shared physics for Minecraft entities moving through Subnautica's world, in blocks per tick.</summary>
    public abstract class McEntity : MonoBehaviour
    {
        public Vector3 Vel;           // blocks per tick
        public bool OnGround, InWater;
        public int Age;
        protected float tickAcc;
        protected Vector3 prevPos, curPos;
        public float Radius = 0.125f;

        protected virtual void Start() { prevPos = curPos = transform.position; }

        protected virtual void Update()
        {
            if (Time.timeScale == 0f || Screens.PausesGame) return;
            tickAcc += Time.deltaTime;
            int guard = 0;
            while (tickAcc >= 0.05f && guard++ < 5)
            {
                tickAcc -= 0.05f;
                prevPos = curPos;
                Tick();
                curPos = transform.position;
                if (this == null || !gameObject) return;
            }
            Render(tickAcc / 0.05f);
        }

        protected abstract void Tick();
        protected virtual void Render(float partial) { }

        /// <summary>Moves by Vel with sliding collisions against blocks and Subnautica geometry.</summary>
        protected bool MoveCollide()
        {
            InWater = SrWorld.IsWater(transform.position) || BlockWorld.Get(BlockWorld.CellAt(transform.position))?.Block.Id == "water";
            var from = transform.position;
            var d = Vel;
            float len = d.magnitude;
            if (len < 1e-5f) { OnGround = Grounded(); return false; }
            bool hitAny = false;
            for (int i = 0; i < 3 && len > 1e-5f; i++)
            {
                if (Physics.SphereCast(from, Radius, d / len, out var hit, len, SrWorld.SolidMask, QueryTriggerInteraction.Ignore) && !IgnoreHit(hit))
                {
                    hitAny = true;
                    from += d / len * Mathf.Max(0, hit.distance - 0.01f);
                    // remove the velocity going into the surface
                    float into = Vector3.Dot(Vel, hit.normal);
                    if (into < 0) Vel -= hit.normal * into;
                    var remain = d - d / len * hit.distance;
                    remain -= hit.normal * Vector3.Dot(remain, hit.normal);
                    d = remain; len = d.magnitude;
                    OnHit(hit);
                }
                else { from += d; break; }
            }
            transform.position = from;
            OnGround = Grounded();
            return hitAny;
        }

        protected virtual bool IgnoreHit(RaycastHit h) => h.collider.transform.IsChildOf(transform) || SrWorld.Player != null && h.collider.transform.IsChildOf(SrWorld.Player.transform);
        protected virtual void OnHit(RaycastHit h) { }

        protected bool Grounded() => Physics.SphereCast(transform.position + Vector3.up * 0.05f, Radius * 0.9f, Vector3.down, out var h, 0.08f, SrWorld.SolidMask, QueryTriggerInteraction.Ignore) && !IgnoreHit(h);
    }

    /// <summary>
    /// A dropped item (ItemEntity): falls with gravity 0.04, drag 0.98, slides with block friction, floats up in
    /// water like Minecraft's applyWaterBuoyancy, spins and bobs, merges with equal stacks nearby, is picked up when
    /// the player walks over it after its pickup delay, and despawns after 5 minutes.
    /// </summary>
    public class ItemEntity : McEntity
    {
        public ItemStack Stack;
        private float pickupDelay;
        private Transform visual;
        private float bob;
        public static readonly List<ItemEntity> All = new List<ItemEntity>();

        public static ItemEntity Spawn(ItemStack s, Vector3 at, Vector3 vel, float pickupDelaySeconds = 0.5f)
        {
            if (s == null || s.Count <= 0) return null;
            var go = new GameObject("mc item " + s.Id);
            go.transform.position = at;
            var e = go.AddComponent<ItemEntity>();
            e.Stack = s;
            e.Vel = vel;
            e.pickupDelay = pickupDelaySeconds * 20f;
            e.bob = Random.value * Mathf.PI * 2f;
            var holder = new GameObject("visual");
            holder.transform.SetParent(go.transform, false);
            var v = ItemRender.Build(s, holder.transform, 0, "ground");
            foreach (var r in v.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            e.visual = holder.transform;
            Sky.Apply(go);
            All.Add(e);
            return e;
        }

        public static void ClearAll()
        {
            foreach (var e in All.ToArray()) if (e != null) Destroy(e.gameObject);
            All.Clear();
        }

        private void OnDestroy() => All.Remove(this);

        protected override void Tick()
        {
            Age++;
            if (Age > 6000) { Destroy(gameObject); return; }
            if (pickupDelay > 0) pickupDelay--;
            // ItemEntity.tick: buoyancy in water, gravity in air
            if (InWater) { Vel *= 0.99f; if (Vel.y < 0.06f) Vel.y += 5.0E-4f; }
            else Vel.y -= 0.04f;
            MoveCollide();
            float f = 0.98f;
            if (OnGround)
            {
                var under = BlockWorld.Get(BlockWorld.CellAt(transform.position + Vector3.down * 0.2f));
                f = (under != null ? under.Block.Slipperiness : 0.6f) * 0.98f;
            }
            Vel = new Vector3(Vel.x * f, Vel.y * 0.98f, Vel.z * f);
            if (OnGround && Vel.y < 0) Vel.y *= -0.5f;

            if (Age % 20 == 0) Merge();
            TryPickup();
        }

        private void Merge()
        {
            foreach (var o in All)
            {
                if (o == this || o == null || o.Stack == null || !o.Stack.SameItem(Stack)) continue;
                if ((o.transform.position - transform.position).sqrMagnitude > 0.5f * 0.5f) continue;
                if (o.Stack.Count + Stack.Count > Stack.MaxStack) continue;
                Stack.Count += o.Stack.Count;
                o.Stack.Count = 0;
                Destroy(o.gameObject);
            }
        }

        private void TryPickup()
        {
            if (pickupDelay > 0 || Screens.Open || SrWorld.Player == null || Motor.SuspendedByGame || Health.Hp <= 0) return;
            var feet = SrWorld.Feet;
            var d = (feet + Vector3.up * 0.9f) - transform.position;
            // the player's box grown by 1 block sideways and 0.5 up and down
            if (Mathf.Abs(d.x) < 1.3f && Mathf.Abs(d.z) < 1.3f && transform.position.y > feet.y - 0.5f && transform.position.y < feet.y + 2.3f)
            {
                int took = Inventory.Add(Stack);
                if (took > 0)
                {
                    Sounds.Play("entity.item.pickup", transform.position, 0.2f, ((Random.value - Random.value) * 0.7f + 1f) * 2f);
                    Stack.Count -= took;
                    if (Stack.Count <= 0) StartCoroutine(FlyIn(SrWorld.Player.transform));
                }
            }
        }

        protected override void Render(float partial)
        {
            transform.position = Vector3.Lerp(prevPos, curPos, partial);
            float t = Age + partial;
            visual.localRotation = Quaternion.Euler(0, (t * 4.5f) % 360f * -1f, 0);
            // ItemEntityRenderer: bob sin((age + td) / 10 + uniqueOffset) * 0.1 + 0.1, plus the model's ground transform
            visual.localPosition = new Vector3(0, Mathf.Sin(t / 10f + bob) * 0.1f + 0.1f + 0.15f, 0);
        }

        private System.Collections.IEnumerator FlyIn(Transform target)
        {
            enabled = false;
            var start = transform.position;
            for (float t = 0; t < 1f; t += Time.deltaTime / 0.15f)
            {
                if (target == null) break;
                transform.position = Vector3.Lerp(start, target.position - Vector3.up * 0.5f, t);
                yield return null;
            }
            Destroy(gameObject);
        }
    }

    /// <summary>ExperienceOrbEntity: drifts towards the player within 8 blocks, gives its points (and mends Mending gear).</summary>
    public class XpOrb : McEntity
    {
        public int Value;
        private Transform quad;
        private static Material mat;

        /// <summary>Splits a total into Minecraft's orb sizes (ExperienceOrbEntity.roundToOrbSize).</summary>
        public static void Spawn(Vector3 at, int total)
        {
            int[] sizes = { 2477, 1237, 617, 307, 149, 73, 37, 17, 7, 3, 1 };
            while (total > 0)
            {
                int v = 1;
                foreach (var s in sizes) if (total >= s) { v = s; break; }
                total -= v;
                var go = new GameObject("xp orb");
                go.transform.position = at;
                var o = go.AddComponent<XpOrb>();
                o.Value = v;
                o.Vel = new Vector3((Random.value * 0.2f - 0.1f) * 2f, Random.value * 0.2f * 2f, (Random.value * 0.2f - 0.1f) * 2f);
                o.Build();
            }
        }

        private int Icon()
        {
            int[] sizes = { 2477, 1237, 617, 307, 149, 73, 37, 17, 7, 3 };
            for (int i = 0; i < sizes.Length; i++) if (Value >= sizes[i]) return 10 - i;
            return 0;
        }

        private void Build()
        {
            if (mat == null) { mat = BlockWorld.MakeMat(1, McTex.Get("entity/experience_orb")); mat.EnableKeyword("_EMISSION"); mat.SetColor("_EmissionColor", new Color(0.4f, 0.6f, 0.1f)); mat.SetTexture("_EmissionMap", McTex.Get("entity/experience_orb")); }
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(q.GetComponent<Collider>());
            q.transform.SetParent(transform, false);
            q.transform.localScale = Vector3.one * 0.3f;
            var mr = q.GetComponent<MeshRenderer>();
            mr.material = mat;
            int icon = Icon();
            mr.material.mainTextureScale = new Vector2(0.25f, 0.25f);
            mr.material.mainTextureOffset = new Vector2((icon % 4) * 0.25f, 0.75f - (icon / 4) * 0.25f);
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            quad = q.transform;
        }

        protected override void Tick()
        {
            Age++;
            if (Age > 6000) { Destroy(gameObject); return; }
            if (InWater) { Vel *= 0.99f; Vel.y += 5.0E-4f; } else Vel.y -= 0.03f;
            var p = SrWorld.Player;
            if (p != null && !Motor.SuspendedByGame)
            {
                var target = SrWorld.Feet + Vector3.up * 0.5f;
                var d = target - transform.position;
                float dist = d.magnitude / 8f;
                if (dist < 1f)
                {
                    float e = 1f - dist;
                    Vel += d.normalized * e * e * 0.1f;
                }
                if (d.magnitude < 1f && Age > 10)
                {
                    int left = Value;
                    left = Mend(left);
                    Xp.Add(left);
                    Sounds.Play("entity.experience_orb.pickup", transform.position, 0.1f, 0.5f * ((Random.value - Random.value) * 0.7f + 1.8f));
                    Destroy(gameObject);
                    return;
                }
            }
            MoveCollide();
            float f = OnGround ? 0.6f * 0.98f : 0.98f;
            Vel = new Vector3(Vel.x * f, Vel.y * 0.98f, Vel.z * f);
            if (OnGround) Vel.y *= -0.9f;
        }

        /// <summary>Mending: each point repairs 2 durability of a damaged Mending item.</summary>
        private static int Mend(int xp)
        {
            var cands = new List<int>();
            for (int i = 0; i < 4; i++) if (Inventory.Armor[i] != null && Inventory.Armor[i].Level("mending") > 0 && Inventory.Armor[i].Damage > 0) cands.Add(i);
            var h = Inventory.Held;
            if (h != null && h.Level("mending") > 0 && h.Damage > 0) cands.Add(-1);
            if (cands.Count == 0) return xp;
            int pick = cands[Random.Range(0, cands.Count)];
            var s = pick < 0 ? h : Inventory.Armor[pick];
            int repair = Mathf.Min(xp * 2, s.Damage);
            s.Damage -= repair;
            return xp - repair / 2;
        }

        protected override void Render(float partial)
        {
            transform.position = Vector3.Lerp(prevPos, curPos, partial);
            if (quad != null && Camera.main != null)
            {
                quad.rotation = Camera.main.transform.rotation;
                float t = Age + partial;
                float g = (Mathf.Sin(t / 2f) + 1f) * 0.5f;
                quad.GetComponent<MeshRenderer>().material.color = new Color(1f, 1f, g * 0.2f + 0.8f * 0f + 0.3f, 1f);
            }
        }
    }

    /// <summary>FallingBlockEntity: sand and gravel without support fall and land as a block (or drop as an item).</summary>
    public class FallingBlock : McEntity
    {
        public BState State;
        private GameObject vis;

        public static void Begin(Vector3Int cell, BState s)
        {
            BlockWorld.Set(cell, null, true);
            var go = new GameObject("falling " + s.Block.Id);
            go.transform.position = BlockWorld.Center(cell);
            var f = go.AddComponent<FallingBlock>();
            f.State = s;
            f.Radius = 0.49f;
            var mb = new MeshBuilder(1);
            foreach (var r in McData.ModelsFor(s)) foreach (var q in ModelBaker.Bake(r, s.Block.Tint)) mb.Add(q, -Vector3.one * 0.5f);
            f.vis = new GameObject("vis");
            f.vis.transform.SetParent(go.transform, false);
            f.vis.AddComponent<MeshFilter>().sharedMesh = mb.Build("falling");
            f.vis.AddComponent<MeshRenderer>().sharedMaterial = BlockWorld.Materials[0];
        }

        protected override void Tick()
        {
            Age++;
            Vel.y -= 0.04f;
            if (InWater) Vel *= 0.98f;
            MoveCollide();
            Vel *= 0.98f;
            var cell = BlockWorld.CellAt(transform.position);
            if (OnGround || BlockWorld.Has(cell + Vector3Int.down) && transform.position.y - cell.y < 0.55f)
            {
                if (!BlockWorld.Has(cell))
                {
                    var s = State;
                    if (s.Block.Class == "ConcretePowderBlock" && SrWorld.IsWater(transform.position)) s = McData.Block(s.Block.Id.Replace("_powder", "")).Default;
                    BlockWorld.Set(cell, s);
                    Sounds.Play(Sounds.BlockEvent(s.Block.Sound, "place"), BlockWorld.Center(cell), 1f, 0.8f);
                }
                else if (State.Block.Item != null) ItemEntity.Spawn(new ItemStack(State.Block.Item), transform.position, Vector3.zero);
                Destroy(gameObject);
            }
            if (Age > 600) Destroy(gameObject);
        }

        protected override void Render(float partial) => transform.position = Vector3.Lerp(prevPos, curPos, partial);
    }
}
