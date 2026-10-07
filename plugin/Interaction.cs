using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>What the crosshair is on: a Minecraft block, a Minecraft mob, a Slime Rancher actor, object or terrain.</summary>
    public struct Target
    {
        public bool Any;
        public bool IsBlock;
        public Vector3Int Cell;
        public Dir Face;
        public Vector3 Point, Normal;
        public float Distance;
        public Mob Mob;
        public Identifiable Actor;      // a Slime Rancher slime, plort, fruit, veggie, chicken... that can be grabbed
        public GameObject SrObject;     // Slime Rancher object hit
        public bool Terrain;
        public RaycastHit Hit;
    }

    /// <summary>
    /// Minecraft's controls in Slime Rancher: left click attacks (PlayerEntity.attack with the cooldown, critical
    /// hits, sweeping, Sharpness/Knockback/Fire Aspect; slimes get knocked back, tarrs pop) and mines (Minecraft
    /// blocks with their real hardness and the cracking overlay; Slime Rancher's rocks, sand and earth give
    /// Minecraft blocks), right click uses: Slime Rancher's buttons, doors and screens, grabbing slimes, plorts,
    /// fruits and chickens, putting them down and feeding slimes, Minecraft blocks (doors, chests, crafting
    /// tables...), placing blocks, eating, bows, tridents, crossbows, buckets, fireworks, armour, spawn eggs,
    /// throwables, fishing. A/Q drops, middle click picks a block.
    /// </summary>
    public static class Interaction
    {
        public static Target Current;
        public static bool Using;
        public static string UseAction;
        public static int UseTicksLeft, UseMax, UseElapsed;
        private static float tickAcc;
        private static bool leftWas, rightWas, leftLatch, rightLatch;
        private static int rightDelay, breakCooldown;
        private static readonly Dictionary<int, float> tarrDamage = new Dictionary<int, float>();

        // mining
        private static Vector3Int miningCell; private static bool mining, miningTerrain;
        private static float progress; private static int hitSoundTicks;
        private static BlockDef terrainBlock; private static bool terrainOre; private static Vector3 terrainPoint, terrainNormal;
        private static GameObject crack; private static MeshRenderer crackR; private static int crackStage = -1;

        public static float Reach => GameMode.Creative ? 5f : 4.5f;

        public static Dictionary<string, float> Predicates(ItemStack s)
        {
            if (s == null || !s.IsMc) return null;
            var d = new Dictionary<string, float>();
            if (Using && Inventory.Held == s)
            {
                d["pulling"] = 1;
                if (s.Is("bow")) { float f = UseElapsed / 20f; d["pull"] = Mathf.Min(1f, f); }
                if (s.Is("crossbow")) d["pull"] = Mathf.Min(1f, UseElapsed / CrossbowPullTime(s));
                if (s.Is("trident")) d["throwing"] = 1;
            }
            if (s.Is("crossbow") && CrossbowCharged(s)) { d["charged"] = 1; if (s.CustomName == null && s.Flight > 0) d["firework"] = 1; }
            return d;
        }

        public static float CrossbowPullTime(ItemStack s) => s == null ? 25 : 25 - 5 * s.Level("quick_charge");
        public static bool CrossbowCharged(ItemStack s) => s != null && s.Potion == "charged";

        public static void Reset()
        {
            Using = false; mining = false; progress = 0;
            tarrDamage.Clear();
            if (crack != null) Object.Destroy(crack);
        }

        // ------------------------------------------------------------------------------------------------ targeting

        // Slime Rancher's solid world plus every actor layer: Actor, Launched, ActorStatic, ActorIgnorePlayer (plorts,
        // fruits and veggies lying around, which the player walks through) and ActorEchoes.
        private const int WorldMask = -675375893 | (1 << 15) | (1 << 9) | (1 << 21) | (1 << 0) | (1 << 11) | (1 << 16);

        /// <summary>A Slime Rancher thing Steve can grab: anything the vacpack could take right now.</summary>
        private static Identifiable Grabbable(Collider col)
        {
            var id = col.GetComponentInParent<Identifiable>();
            if (id == null || Identifiable.IsGordo(id.id)) return null;
            var vac = id.GetComponent<Vacuumable>();
            if (vac == null || !vac.enabled) return null;
            return id;
        }

        private static bool Ignored(Collider c)
        {
            if (c == null || BlockWorld.CellOf(c, out _)) return true;
            var pl = SrWorld.Player;
            if (pl != null && c.transform.IsChildOf(pl.transform)) return true;
            if (c.GetComponentInParent<Mob>() != null || c.GetComponentInParent<McEntity>() != null) return true;
            if (View.I != null && View.I.Body != null && c.transform.IsChildOf(View.I.Body.transform)) return true;
            return false;
        }

        private static Target Raycast()
        {
            var t = new Target();
            var cam = Camera.main.transform;
            var origin = cam.position;
            var dir = cam.forward;
            float reach = Reach;
            // 1. Minecraft blocks by their outline shapes
            if (BlockRay(origin, dir, reach, out var cell, out var face, out var pt, out var dist))
            {
                t.Any = true; t.IsBlock = true; t.Cell = cell; t.Face = face; t.Point = pt; t.Distance = dist;
                t.Normal = (Vector3)BlockWorld.Offset(face);
                reach = dist;
            }
            // 2. Minecraft mobs (3 blocks like Minecraft's entity reach, 5 in creative... the block reach caps it)
            var mob = Mobs.Raycast(origin, dir, Mathf.Min(reach, GameMode.Creative ? 5f : 3f), out float md);
            if (mob != null) { t = new Target { Any = true, Mob = mob, Distance = md, Point = origin + dir * md }; reach = md; }
            // 3. Slime Rancher's world: actors first, then solid ground and objects
            var hits = Physics.RaycastAll(origin, dir, reach, WorldMask, QueryTriggerInteraction.Collide);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            Target sr = default;
            foreach (var h in hits)
            {
                if (Ignored(h.collider)) continue;
                var id = Grabbable(h.collider);
                if (id != null) { sr = new Target { Any = true, Hit = h, Point = h.point, Normal = h.normal, Distance = h.distance, SrObject = id.gameObject, Actor = id }; break; }
                if (h.collider.isTrigger) continue;
                sr = new Target { Any = true, Hit = h, Point = h.point, Normal = h.normal, Distance = h.distance, SrObject = h.collider.gameObject, Terrain = SrWorld.IsTerrain(h.collider) };
                break;
            }
            // Minecraft entity hitboxes are generous: a fruit hanging in a tree or a carrot in the soil sits half
            // inside other colliders, so also sweep a small sphere for anything grabbable just as close.
            if (sr.Actor == null)
            {
                float limit = sr.Any ? sr.Distance + 0.35f : reach;
                foreach (var h in Physics.SphereCastAll(origin, 0.3f, dir, Mathf.Min(limit, reach), WorldMask, QueryTriggerInteraction.Collide))
                {
                    if (Ignored(h.collider)) continue;
                    var id = Grabbable(h.collider);
                    if (id == null) continue;
                    if (sr.Actor == null || h.distance < sr.Distance)
                    {
                        var point = h.point == Vector3.zero ? id.transform.position : h.point;
                        sr = new Target { Any = true, Hit = h, Point = point, Normal = -dir, Distance = h.distance, SrObject = id.gameObject, Actor = id };
                    }
                }
            }
            if (sr.Any && sr.Distance <= reach) t = sr;
            return t;
        }

        /// <summary>Voxel walk along the ray testing each cell's Minecraft outline boxes (BlockView.raycast).</summary>
        public static bool BlockRay(Vector3 origin, Vector3 dir, float max, out Vector3Int cell, out Dir face, out Vector3 point, out float dist)
        {
            cell = default; face = Dir.None; point = default; dist = max;
            bool found = false;
            var c = BlockWorld.CellAt(origin);
            int stepX = dir.x > 0 ? 1 : -1, stepY = dir.y > 0 ? 1 : -1, stepZ = dir.z > 0 ? 1 : -1;
            float tMaxX = dir.x != 0 ? ((dir.x > 0 ? c.x + 1 : c.x) - origin.x) / dir.x : float.MaxValue;
            float tMaxY = dir.y != 0 ? ((dir.y > 0 ? c.y + 1 : c.y) - origin.y) / dir.y : float.MaxValue;
            float tMaxZ = dir.z != 0 ? ((dir.z > 0 ? c.z + 1 : c.z) - origin.z) / dir.z : float.MaxValue;
            float tdX = dir.x != 0 ? Mathf.Abs(1f / dir.x) : float.MaxValue, tdY = dir.y != 0 ? Mathf.Abs(1f / dir.y) : float.MaxValue, tdZ = dir.z != 0 ? Mathf.Abs(1f / dir.z) : float.MaxValue;
            for (int i = 0; i < 64; i++)
            {
                var s = BlockWorld.Get(c);
                if (s != null)
                {
                    foreach (var b in s.Outline)
                    {
                        var wb = BlockWorld.ToWorld(b, c);
                        if (wb.IntersectRay(new Ray(origin, dir), out float d) && d <= dist)
                        {
                            dist = d; cell = c; found = true;
                            point = origin + dir * d;
                            var local = point - wb.center;
                            var e = wb.extents;
                            float ax = Mathf.Abs(local.x / e.x), ay = Mathf.Abs(local.y / e.y), az = Mathf.Abs(local.z / e.z);
                            if (ax >= ay && ax >= az) face = local.x > 0 ? Dir.East : Dir.West;
                            else if (ay >= az) face = local.y > 0 ? Dir.Up : Dir.Down;
                            else face = local.z > 0 ? Dir.North : Dir.South;
                        }
                    }
                    if (found) return true;
                }
                float tNext = Mathf.Min(tMaxX, Mathf.Min(tMaxY, tMaxZ));
                if (tNext > max) break;
                if (tMaxX < tMaxY && tMaxX < tMaxZ) { c.x += stepX; tMaxX += tdX; }
                else if (tMaxY < tMaxZ) { c.y += stepY; tMaxY += tdY; }
                else { c.z += stepZ; tMaxZ += tdZ; }
            }
            return found;
        }

        // ------------------------------------------------------------------------------------------------ frame

        public static void Update()
        {
            if (SrWorld.Player == null || Camera.main == null) return;
            bool blocked = Screens.Open || Chat.Open || Motor.SuspendedByGame || !Game.Gameplay();
            Current = blocked ? new Target() : Raycast();

            bool left = !blocked && Input.GetMouseButton(0), right = !blocked && Input.GetMouseButton(1);
            // a click is kept until a 20 Hz tick handles it, so a quick click between two ticks is not lost
            if (left && !leftWas) leftLatch = true;
            if (right && !rightWas) rightLatch = true;
            if (blocked) { leftLatch = rightLatch = false; }
            bool leftDown = leftLatch, rightDown = rightLatch;
            leftWas = left; rightWas = right;

            if (!blocked)
            {
                // hotbar: number keys and the wheel
                for (int i = 0; i < 9; i++) if (Input.GetKeyDown(KeyCode.Alpha1 + i)) Select(i);
                float wheel = Input.mouseScrollDelta.y;
                if (wheel > 0.01f) Select((Inventory.Selected + 8) % 9);
                else if (wheel < -0.01f) Select((Inventory.Selected + 1) % 9);
                if (Input.GetKeyDown(Game.DropKey)) Drop(Input.GetKey(KeyCode.LeftControl));
                if (Input.GetMouseButtonDown(2)) PickBlock();
                // F swaps hands, unless Slime Rancher's flashlight key is F: then it stays the flashlight
                if (Input.GetKeyDown(KeyCode.G)) SwapHands();
            }

            // Slime Rancher's own buttons, doors, plots and screens take the right click when one is under the cursor.
            if (rightDown && GamePatches.UiTargeted && !t_IsBlockOrMob())
            {
                GamePatches.RequestInteract();
                View.I?.Swing();
                rightLatch = false;
                rightDelay = 4;
                return;
            }

            tickAcc += Time.deltaTime;
            int guard = 0;
            while (tickAcc >= 0.05f && guard++ < 4) { tickAcc -= 0.05f; Tick(left || leftDown, right || rightDown, leftDown, rightDown); leftDown = false; rightDown = false; leftLatch = rightLatch = false; }
            UpdateCrack();
        }

        private static bool t_IsBlockOrMob() => Current.IsBlock || Current.Mob != null;

        private static void Select(int i)
        {
            if (i == Inventory.Selected) return;
            Inventory.Selected = i;
            StopUsing(false);
            Hud.ShowItemName();
        }

        private static void Tick(bool left, bool right, bool leftDown, bool rightDown)
        {
            if (rightDelay > 0) rightDelay--;
            if (breakCooldown > 0) breakCooldown--;
            var t = Current;

            // ---- item use in progress
            if (Using)
            {
                if (!right) { ReleaseUse(); }
                else UseTick();
                return;
            }

            // ---- left: attack / mine
            if (left)
            {
                if (leftDown)
                {
                    if (t.Mob != null || t.Actor != null) { Attack(t); return; }
                    if (!t.Any) { View.I?.Swing(); View.I?.ResetAttackCooldown(); }
                }
                if (t.IsBlock || t.Terrain) Mine(t);
                else StopMining();
            }
            else StopMining();

            // ---- right: use
            if (right && rightDelay == 0)
            {
                rightDelay = 4; // Minecraft's itemUseCooldown
                Use(t, rightDown);
            }
        }

        // ------------------------------------------------------------------------------------------------ attack

        /// <summary>PlayerEntity.attack.</summary>
        public static void Attack(Target t)
        {
            var v = View.I;
            var held = Inventory.Held;
            float baseDmg = held != null && held.IsMc ? held.Item.AttackDamage : 1f;
            if (Effects.Has("strength")) baseDmg += 3f * (Effects.Amplifier("strength") + 1);
            if (Effects.Has("weakness")) baseDmg -= 4f * (Effects.Amplifier("weakness") + 1);
            bool undead = t.Mob != null && t.Mob.Undead, arthropod = t.Mob != null && t.Mob.Arthropod, aquatic = t.Mob != null && t.Mob.Aquatic;
            float ench = 0;
            if (held != null)
            {
                int sh = held.Level("sharpness"); if (sh > 0) ench += 1f + Mathf.Max(0, sh - 1) * 0.5f;
                if (undead) ench += held.Level("smite") * 2.5f;
                if (arthropod) ench += held.Level("bane_of_arthropods") * 2.5f;
                if (aquatic) ench += held.Level("impaling") * 2.5f;
            }
            float h = v != null ? v.Cooldown(0.5f) : 1f;
            float f = baseDmg * (0.2f + h * h * 0.8f);
            ench *= h;
            v?.ResetAttackCooldown();
            v?.Swing();
            Hunger.Exhaust(0.1f);
            if (f <= 0 && ench <= 0) return;
            bool strong = h > 0.9f;
            int knock = held != null ? held.Level("knockback") : 0;
            bool sprintHit = false;
            var pos = t.Point;
            if (Motor.Sprinting && strong) { Sounds.Play("entity.player.attack.knockback", pos); knock++; sprintHit = true; }
            bool crit = strong && Motor.FallDistance > 0 && !Motor.OnGround && !Motor.InWater && !Effects.Has("blindness") && !Motor.Sprinting;
            if (crit) f *= 1.5f;
            f += ench;
            bool sweep = strong && !crit && !sprintHit && Motor.OnGround && held != null && held.IsMc && held.Item.Class == "SwordItem";
            var yaw = Camera.main.transform.forward; yaw.y = 0; yaw.Normalize();

            bool hit = DealDamage(t, f, knock, yaw, held);
            if (!hit) { Sounds.Play("entity.player.attack.nodamage", pos); return; }
            if (sweep)
            {
                // SweepingEnchantment: 1 + level/(level+1) * damage to everything around the target
                int sl = held.Level("sweeping");
                float sweepDmg = 1f + (sl > 0 ? sl / (float)(sl + 1) : 0f) * f;
                foreach (var m in Mobs.Near(pos, 1.5f)) if (m != t.Mob) m.Damage(sweepDmg, "player", 0.4f, yaw);
                Sounds.Play("entity.player.attack.sweep", pos);
                Particles.Sweep(pos, yaw);
            }
            if (crit) { Sounds.Play("entity.player.attack.crit", pos); Particles.Crit(pos); }
            else if (!sweep) Sounds.Play(strong ? "entity.player.attack.strong" : "entity.player.attack.weak", pos);
            if (ench > 0) Particles.EnchantedHit(pos);
            if (held != null && held.Damageable) Inventory.DamageHeld(held.Item.Class == "SwordItem" || held.Item.Class == "TridentItem" ? 1 : held.Item.IsTool ? 2 : 0);
        }

        private static bool DealDamage(Target t, float dmg, int knock, Vector3 dir, ItemStack held)
        {
            int fire = held != null ? held.Level("fire_aspect") : 0;
            if (t.Mob != null)
            {
                if (fire > 0) t.Mob.SetOnFire(fire * 4);
                return t.Mob.Damage(dmg, "player", 0.4f + knock * 0.5f, dir);
            }
            if (t.Actor != null)
            {
                // Slime Rancher actors: Minecraft's knockback (0.4 blocks/tick and up), and tarrs, which no water gun
                // washes away any more, pop once they have taken 10 Minecraft health points of damage.
                var rb = t.Actor.GetComponent<Rigidbody>();
                if (rb != null && !rb.isKinematic) rb.AddForce((dir * (0.4f + knock * 0.5f) + Vector3.up * 0.2f) * 20f, ForceMode.VelocityChange);
                var id = t.Actor.id;
                if (Identifiable.IsSlime(id)) Sounds.Play(Identifiable.IsLargo(id) ? "entity.slime.hurt" : "entity.slime.hurt_small", t.Point);
                if (Identifiable.IsTarr(id))
                {
                    int key = t.Actor.gameObject.GetInstanceID();
                    tarrDamage.TryGetValue(key, out var taken);
                    taken += dmg;
                    if (taken >= 10f)
                    {
                        tarrDamage.Remove(key);
                        var at = t.Actor.transform.position;
                        Particles.Break(at, McData.Block("black_concrete_powder")?.Default);
                        Sounds.Play("entity.slime.death", at);
                        Destroyer.DestroyActor(t.Actor.gameObject, "SlimeCraft.Attack");
                        XpOrb.Spawn(at, 3);
                    }
                    else tarrDamage[key] = taken;
                }
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------------ mining

        private static void Mine(Target t)
        {
            var held = Inventory.Held;
            if (t.IsBlock)
            {
                var s = BlockWorld.Get(t.Cell);
                if (s == null) { StopMining(); return; }
                if (GameMode.Creative)
                {
                    if (breakCooldown > 0) return;
                    if (held != null && held.IsMc && held.Item.Class == "SwordItem") return; // swords cannot break blocks in creative
                    View.I?.Swing();
                    BreakBlock(t.Cell, held, false);
                    breakCooldown = 5;
                    return;
                }
                if (!mining || miningTerrain || miningCell != t.Cell) { mining = true; miningTerrain = false; miningCell = t.Cell; progress = 0; hitSoundTicks = 0; }
                progress += Mining.Delta(s.Block, held);
                View.I?.Swing();
                if (hitSoundTicks++ % 4 == 0) Sounds.Play(Sounds.BlockEvent(s.Block.Sound, "hit"), BlockWorld.Center(t.Cell), 0.25f, 0.5f);
                Particles.Hit(t.Point, t.Normal, s);
                if (progress >= 1f)
                {
                    BreakBlock(t.Cell, held, false);
                    StopMining();
                    breakCooldown = 5;
                }
                return;
            }
            // Slime Rancher's ground and rocks give Minecraft blocks (the scenery itself stays)
            if (!mining || !miningTerrain || (terrainPoint - t.Point).sqrMagnitude > 0.5f)
            {
                mining = true; miningTerrain = true; progress = 0; hitSoundTicks = 0;
                terrainPoint = t.Point; terrainNormal = t.Normal;
                terrainBlock = SrWorld.MinedBlock(t.Hit, out terrainOre);
            }
            if (terrainBlock == null) return;
            View.I?.Swing();
            float delta = GameMode.Creative ? 1f : Mining.Delta(terrainBlock, held);
            progress += delta;
            if (hitSoundTicks++ % 4 == 0) Sounds.Play(Sounds.BlockEvent(terrainBlock.Sound, "hit"), t.Point, 0.25f, 0.5f);
            Particles.Hit(t.Point, t.Normal, terrainBlock.Default);
            if (progress >= 1f)
            {
                Sounds.Play(Sounds.BlockEvent(terrainBlock.Sound, "break"), t.Point, 1f, 0.8f);
                Particles.Break(t.Point - t.Normal * 0.5f, terrainBlock.Default);
                if (!GameMode.Creative)
                {
                    foreach (var d in Loot.BlockDrops(terrainBlock.Default, held)) ItemEntity.Spawn(d, t.Point + t.Normal * 0.3f, Random.insideUnitSphere * 0.1f, 0.5f);
                    int xp = Loot.BlockXp(terrainBlock, held);
                    if (xp > 0) XpOrb.Spawn(t.Point + t.Normal * 0.3f, xp);
                }
                if (held != null && held.Damageable && held.Item.IsTool) Inventory.DamageHeld(1);
                Hunger.Exhaust(0.005f);
                StopMining();
                breakCooldown = 5;
            }
        }

        public static void StopMining()
        {
            mining = false; progress = 0;
            if (crack != null) crack.SetActive(false);
        }

        /// <summary>Breaks a Minecraft block (player mining, explosions, lost support), with drops, sound, particles, xp.</summary>
        public static void BreakBlock(Vector3Int c, ItemStack tool, bool byUpdate, bool explosion = false)
        {
            var s = BlockWorld.Get(c);
            if (s == null) return;
            var center = BlockWorld.Center(c);
            Sounds.Play(Sounds.BlockEvent(s.Block.Sound, "break"), center, 1f, 0.8f);
            Particles.Break(center, s);
            // containers spill their contents
            var e = BlockWorld.Entity(c);
            bool shulker = s.Block.Class == "ShulkerBoxBlock";
            if (e?.Items != null && !shulker)
                foreach (var it in e.Items) if (it != null) ItemEntity.Spawn(it, center, Random.insideUnitSphere * 0.1f, 0.5f);
            if (!GameMode.Creative || byUpdate)
            {
                var drops = Loot.BlockDrops(s, byUpdate ? null : tool, explosion);
                if (byUpdate && drops.Count == 0 && s.Block.Item != null && !s.Block.ToolRequired) drops.Add(new ItemStack(s.Block.Item));
                foreach (var d in drops)
                {
                    if (shulker && d.IsMc && d.Item.Id.EndsWith("shulker_box") && e?.Items != null) d.Contents = e.Items;
                    ItemEntity.Spawn(d, center, new Vector3(Random.Range(-0.1f, 0.1f), 0.2f, Random.Range(-0.1f, 0.1f)), 0.5f);
                }
                if (!byUpdate)
                {
                    int xp = Loot.BlockXp(s.Block, tool);
                    if (xp > 0) XpOrb.Spawn(center, xp);
                    Hunger.Exhaust(0.005f);
                    if (tool != null && tool.Damageable && tool.Item.IsTool && s.Block.Hardness > 0) Inventory.DamageHeld(1);
                }
            }
            Special.Forget(c);
            // double blocks: the other half goes too
            if (s.Block.Class == "DoorBlock" || s.Block.Class == "TallPlantBlock" || s.Block.Class == "TallFlowerBlock" || s.Block.Class == "TallSeagrassBlock")
            {
                var other = c + (s["half"] == "lower" ? Vector3Int.up : Vector3Int.down);
                var o = BlockWorld.Get(other);
                if (o != null && o.Block == s.Block) BlockWorld.Remove(other, false);
            }
            if (s.Block.Class == "BedBlock")
            {
                var f = BlockRules.ParseDir(s["facing"]);
                var other = c + BlockWorld.Offset(s["part"] == "foot" ? f : BlockWorld.Opposite(f));
                var o = BlockWorld.Get(other);
                if (o != null && o.Block == s.Block) BlockWorld.Remove(other, false);
            }
            BlockWorld.Remove(c);
        }

        private static void UpdateCrack()
        {
            if (!mining || progress <= 0) { if (crack != null) crack.SetActive(false); return; }
            int stage = Mathf.Clamp((int)(progress * 10f), 0, 9);
            if (crack == null)
            {
                crack = new GameObject("mc crack");
                Object.DontDestroyOnLoad(crack);
                var mf = crack.AddComponent<MeshFilter>();
                mf.sharedMesh = ModelBaker.ModelMesh("block/stone", -1, out _);
                crackR = crack.AddComponent<MeshRenderer>();
                crackR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            crack.SetActive(true);
            if (stage != crackStage)
            {
                crackStage = stage;
                var m = BlockWorld.MakeMat(1, Atlas.Source("block/destroy_stage_" + stage));
                m.SetFloat("_Cutoff", 0.1f);
                if (crackR.sharedMaterial != null) Object.Destroy(crackR.sharedMaterial);
                crackR.sharedMaterial = m;
            }
            if (miningTerrain)
            {
                crack.transform.position = terrainPoint - terrainNormal * 0.49f;
                crack.transform.rotation = Quaternion.LookRotation(terrainNormal);
                crack.transform.localScale = new Vector3(1.002f, 1.002f, 1f);
            }
            else
            {
                crack.transform.position = BlockWorld.Center(miningCell);
                crack.transform.rotation = Quaternion.identity;
                crack.transform.localScale = Vector3.one * 1.004f;
            }
        }

        // ------------------------------------------------------------------------------------------------ use

        private static void Use(Target t, bool fresh)
        {
            var held = Inventory.Held;
            bool sneaking = Motor.Sneaking;

            // 1. Minecraft block interaction (doors, chests, crafting tables...) unless sneaking with an item
            if (t.IsBlock && (!sneaking || held == null))
            {
                var s = BlockWorld.Get(t.Cell);
                if (s != null && BlockRules.Use(t.Cell, s)) { View.I?.Swing(); return; }
            }
            // 2. Slime Rancher actors: feed a slime what it eats, otherwise grab it into the inventory
            if (t.Actor != null)
            {
                if (fresh)
                {
                    bool slime = Identifiable.IsSlime(t.Actor.id) && !Identifiable.IsTarr(t.Actor.id);
                    if (held != null && held.IsSr && slime && !sneaking && SlimeFood(held.Actor)) Feed(t.Actor.gameObject, held);
                    else Grab(t.Actor);
                }
                return;
            }
            // 3. Minecraft mob interaction (buckets on fish, shears...)
            if (t.Mob != null && held != null && t.Mob.Interact(held)) { View.I?.Swing(); return; }
            if (held == null) return;

            // 4. the item itself
            if (held.IsSr) { UseSrItem(t, held, fresh); return; }
            var id = held.Item.Id;

            // armour: right click puts it on
            if (held.Item.ArmorSlot != null && !t.IsBlock)
            {
                int ai = Inventory.ArmorIndex(held.Item.ArmorSlot);
                if (held.Is("elytra")) ai = 2;
                if (ai >= 0 && fresh)
                {
                    var old = Inventory.Armor[ai];
                    Inventory.Armor[ai] = held.Copy(1);
                    held.Count--;
                    Inventory.Held = held.Count > 0 ? held : old;
                    if (held.Count > 0 && old != null) Inventory.Add(old);
                    Sounds.Play(ArmorSound(Inventory.Armor[ai]), null);
                    View.I?.Swing();
                }
                return;
            }

            switch (id)
            {
                case "bow":
                    if (GameMode.Creative || held.Level("infinity") > 0 || Inventory.CountOf(s => s.Is("arrow") || s.Is("spectral_arrow") || s.Is("tipped_arrow")) > 0) StartUse("bow", 72000);
                    return;
                case "crossbow":
                    if (CrossbowCharged(held)) { ShootCrossbow(held); return; }
                    if (GameMode.Creative || Inventory.CountOf(s => s.Is("arrow") || s.Is("firework_rocket")) > 0) StartUse("crossbow", 72000);
                    return;
                case "trident":
                    if (held.Damage < held.Item.Durability - 1 && (held.Level("riptide") == 0 || Motor.InWater)) StartUse("spear", 72000);
                    return;
                case "shield": StartUse("block", 72000); return;
                case "firework_rocket":
                    if (!fresh) return;
                    if (Motor.FallFlying)
                    {
                        Motor.RocketTicks = 10 * (Mathf.Max(1, held.Flight) + 1) + Random.Range(0, 6) + Random.Range(0, 7);
                        Sounds.Play("entity.firework_rocket.launch", SrWorld.Eye, 3f);
                        Inventory.ShrinkHeld();
                        Particles.Rocket(SrWorld.Eye);
                    }
                    else if (t.Any) { Projectiles.Firework(t.Point + t.Normal * 0.1f, held.Flight); Inventory.ShrinkHeld(); }
                    return;
                case "snowball": case "egg": case "ender_pearl": case "splash_potion": case "lingering_potion": case "experience_bottle":
                    if (!fresh) return;
                    Projectiles.Throw(held);
                    Inventory.ShrinkHeld();
                    View.I?.Swing();
                    return;
                case "fishing_rod":
                    if (fresh) Fishing.Toggle(held);
                    return;
                case "bucket":
                    if (fresh && (Motor.InWater || t.Any && SrWorld.IsWater(t.Point + t.Normal * 0.1f) || t.IsBlock && BlockWorld.Get(t.Cell)?.Block.Id == "water"))
                    {
                        if (t.IsBlock && BlockWorld.Get(t.Cell)?.Block.Id == "water") BlockWorld.Remove(t.Cell);
                        Sounds.Play("item.bucket.fill", SrWorld.Eye);
                        ReplaceHeld(ItemStack.Of("water_bucket"));
                    }
                    return;
                case "water_bucket": case "lava_bucket":
                    if (fresh && t.Any)
                    {
                        var c = t.IsBlock ? t.Cell + BlockWorld.Offset(t.Face) : BlockWorld.CellAt(t.Point + t.Normal * 0.5f);
                        if (!BlockWorld.Has(c))
                        {
                            BlockWorld.Set(c, McData.Block(id == "water_bucket" ? "water" : "lava").Default);
                            Sounds.Play(id == "water_bucket" ? "item.bucket.empty" : "item.bucket.empty_lava", BlockWorld.Center(c));
                            if (!GameMode.Creative) ReplaceHeld(ItemStack.Of("bucket"));
                        }
                    }
                    return;
                case "bone_meal":
                    if (fresh && t.IsBlock && BoneMeal.Apply(t.Cell)) { if (!GameMode.Creative) Inventory.ShrinkHeld(); View.I?.Swing(); }
                    return;
                case "flint_and_steel": case "fire_charge":
                    if (fresh && t.IsBlock)
                    {
                        var s = BlockWorld.Get(t.Cell);
                        if (s.Block.Id == "tnt") { BlockWorld.Remove(t.Cell); Projectiles.Tnt(BlockWorld.Center(t.Cell)); }
                        else if (s.Block.Class == "CampfireBlock" || s.Block.Class == "CandleBlock") BlockWorld.Set(t.Cell, s.With("lit", "true"));
                        else
                        {
                            var up = t.Cell + BlockWorld.Offset(t.Face);
                            if (!BlockWorld.Has(up) && !SrWorld.IsWater(BlockWorld.Center(up))) BlockWorld.Set(up, McData.Block("fire").Default);
                        }
                        Sounds.Play(id == "fire_charge" ? "item.firecharge.use" : "item.flintandsteel.use", BlockWorld.Center(t.Cell));
                        if (id == "flint_and_steel") Inventory.DamageHeld(1); else Inventory.ShrinkHeld();
                        View.I?.Swing();
                    }
                    return;
                case "glass_bottle":
                    if (fresh && (Motor.InWater || t.Any && SrWorld.IsWater(t.Point)))
                    {
                        Sounds.Play("item.bottle.fill", SrWorld.Eye);
                        Inventory.ShrinkHeld();
                        Inventory.Add(new ItemStack(McData.Item("potion")) { Potion = "water" });
                    }
                    return;
            }
            // buckets of fish and axolotls: release the mob (MobBucketItem), the bucket keeps its water
            if (fresh && t.Any && (id == "cod_bucket" || id == "salmon_bucket" || id == "tropical_fish_bucket" || id == "pufferfish_bucket" || id == "axolotl_bucket" || id == "tadpole_bucket"))
            {
                Mobs.Summon(id.Substring(0, id.Length - "_bucket".Length), t.Point + t.Normal * 0.5f);
                Sounds.Play(id == "axolotl_bucket" ? "item.bucket.empty_axolotl" : "item.bucket.empty_fish", t.Point);
                if (!GameMode.Creative) ReplaceHeld(ItemStack.Of("water_bucket"));
                return;
            }
            if (id.EndsWith("_spawn_egg") && fresh && t.Any)
            {
                var type = id.Substring(0, id.Length - "_spawn_egg".Length);
                Mobs.Summon(type, t.Point + t.Normal * 0.5f);
                Inventory.ShrinkHeld();
                return;
            }

            // tools on blocks: axes strip logs, shovels make paths, hoes till
            if (t.IsBlock && fresh && held.Item.IsTool && ToolUse(t, held)) return;

            // block placement
            if (held.Item.Block != null && (t.IsBlock || t.Any)) { Place(t, held); return; }

            // food and drinks
            if (held.Item.Food != null)
            {
                if (Hunger.CanEat(held.Item.Food.Always) && fresh) StartUse(id == "honey_bottle" ? "drink" : "eat", held.Item.Food.Snack ? 16 : id == "honey_bottle" ? 40 : 32);
                return;
            }
            if ((id == "potion" || id == "milk_bucket") && fresh) { StartUse("drink", 32); return; }
        }

        private static string ArmorSound(ItemStack a)
        {
            if (a == null || !a.IsMc) return "item.armor.equip_generic";
            if (a.Is("elytra")) return "item.armor.equip_elytra";
            if (a.Is("turtle_helmet")) return "item.armor.equip_turtle";
            switch (a.Item.ArmorMaterial)
            {
                case "leather": return "item.armor.equip_leather";
                case "chainmail": return "item.armor.equip_chain";
                case "iron": return "item.armor.equip_iron";
                case "gold": return "item.armor.equip_gold";
                case "diamond": return "item.armor.equip_diamond";
                case "netherite": return "item.armor.equip_netherite";
            }
            return "item.armor.equip_generic";
        }

        private static void ReplaceHeld(ItemStack s)
        {
            var h = Inventory.Held;
            if (h != null && h.Count > 1) { h.Count--; Inventory.Add(s); }
            else Inventory.Held = s;
        }

        private static bool ToolUse(Target t, ItemStack held)
        {
            var s = BlockWorld.Get(t.Cell);
            if (s == null) return false;
            var cls = held.Item.Class;
            if (cls == "AxeItem")
            {
                var stripped = McData.Block("stripped_" + s.Block.Id);
                if (stripped != null)
                {
                    var ns = stripped.Default;
                    if (s["axis"] != null) ns = ns.With("axis", s["axis"]);
                    BlockWorld.Set(t.Cell, ns);
                    Sounds.Play("item.axe.strip", BlockWorld.Center(t.Cell));
                    Inventory.DamageHeld(1); View.I?.Swing();
                    return true;
                }
            }
            if (cls == "ShovelItem" && (s.Block.Id == "grass_block" || s.Block.Id == "dirt" || s.Block.Id == "podzol" || s.Block.Id == "mycelium" || s.Block.Id == "coarse_dirt") && !BlockWorld.Has(t.Cell + Vector3Int.up))
            {
                BlockWorld.Set(t.Cell, McData.Block("dirt_path").Default);
                Sounds.Play("item.shovel.flatten", BlockWorld.Center(t.Cell));
                Inventory.DamageHeld(1); View.I?.Swing();
                return true;
            }
            if (cls == "HoeItem" && (s.Block.Id == "grass_block" || s.Block.Id == "dirt" || s.Block.Id == "dirt_path") && !BlockWorld.Has(t.Cell + Vector3Int.up))
            {
                BlockWorld.Set(t.Cell, McData.Block("farmland").Default);
                Sounds.Play("item.hoe.till", BlockWorld.Center(t.Cell));
                Inventory.DamageHeld(1); View.I?.Swing();
                return true;
            }
            return false;
        }

        /// <summary>BlockItem.place: the clicked block's replaceable cell or the neighbour on the clicked face.</summary>
        private static void Place(Target t, ItemStack held)
        {
            Vector3Int cell; Dir side;
            if (t.IsBlock)
            {
                var clicked = BlockWorld.Get(t.Cell);
                bool slabMerge = clicked != null && clicked.Block == held.Item.Block && clicked.Block.Class.Contains("Slab") && clicked["type"] != "double"
                                 && ((clicked["type"] == "bottom" && t.Face == Dir.Up) || (clicked["type"] == "top" && t.Face == Dir.Down));
                bool stack = clicked != null && clicked.Block == held.Item.Block && (clicked.Block.Class == "CandleBlock" || clicked.Block.Class == "SeaPickleBlock" || clicked.Block.Class == "SnowBlock");
                if (clicked != null && (clicked.Block.Replaceable || slabMerge || stack)) { cell = t.Cell; side = t.Face; }
                else { cell = t.Cell + BlockWorld.Offset(t.Face); side = t.Face; }
            }
            else
            {
                // on Slime Rancher geometry: the cell just off the surface, on the side the ray came from
                cell = BlockWorld.CellAt(t.Point + t.Normal * 0.01f);
                var n = t.Normal;
                float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);
                if (ay >= ax && ay >= az) side = n.y > 0 ? Dir.Up : Dir.Down;
                else if (ax >= az) side = n.x > 0 ? Dir.East : Dir.West;
                else side = n.z > 0 ? Dir.North : Dir.South;
            }
            var existing = BlockWorld.Get(cell);
            var block = BlockRules.BlockFor(held.Item, side);
            if (existing != null && !existing.Block.Replaceable && existing.Block != block) return;
            var ctx = new PlaceCtx { Cell = cell, Side = side, Hit = t.Point, Look = BlockRules.McLook(Camera.main.transform.forward), Sneaking = Motor.Sneaking };
            var s = BlockRules.PlacementState(block, ctx);
            if (s == null) return;
            // concrete powder turns to concrete in water, like ConcretePowderBlock
            if (block.Class == "ConcretePowderBlock" && SrWorld.IsWater(BlockWorld.Center(cell))) s = McData.Block(block.Id.Replace("_powder", "")).Default;
            if (s.Block.HasProp("waterlogged") && SrWorld.IsWater(BlockWorld.Center(cell))) s = s.With("waterlogged", "true");
            if (!BlockRules.Supported(s, cell) && s.Block.Class != "DoorBlock" && s.Block.Class != "BedBlock" && !s.Block.Class.StartsWith("Tall")) return;
            // never inside the player or a creature (BlockItem.canPlace with the entity collision check)
            foreach (var b in s.Collision)
            {
                var wb = BlockWorld.ToWorld(b, cell);
                var cols = Physics.OverlapBox(wb.center, wb.extents * 0.98f, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
                foreach (var c in cols)
                {
                    if (c.gameObject.layer == 8 || c.GetComponentInParent<Identifiable>() != null || c.GetComponentInParent<Mob>() != null) return;
                }
            }
            BlockWorld.Set(cell, s, false);
            BlockRules.PlaceCompanion(cell, s);
            BlockRules.NeighbourChanged(cell);
            if (held.Contents != null && s.Block.Class == "ShulkerBoxBlock") BlockWorld.Entity(cell, true, 27).Items = held.Contents;
            Sounds.Play(Sounds.BlockEvent(s.Block.Sound, "place"), BlockWorld.Center(cell), 1f, 0.8f);
            View.I?.Swing();
            Inventory.ShrinkHeld();
        }

        /// <summary>A held Slime Rancher thing: put it down where you aim, or eat a fruit or veggie in the air.</summary>
        private static void UseSrItem(Target t, ItemStack held, bool fresh)
        {
            if (!fresh) return;
            if (t.Any && t.Distance <= Reach)
            {
                PutDown(held, t.Point, t.Normal);
                return;
            }
            if (SrItems.Edible(held.Actor) && Hunger.CanEat(false)) StartUse("eat", 32);
        }

        /// <summary>What slimes eat (and what makes largos): food, chickens, and plorts of other kinds.</summary>
        private static bool SlimeFood(Identifiable.Id food)
        {
            return Identifiable.IsFood(food) || Identifiable.IsPlort(food) || Identifiable.IsChick(food) || Identifiable.IsAnimal(food);
        }

        /// <summary>Right click on a Slime Rancher actor: into the inventory, like the vacpack did.</summary>
        private static void Grab(Identifiable actor)
        {
            var id = actor.id;
            var vac = actor.GetComponent<Vacuumable>();
            if (vac == null || !vac.enabled || vac.isCaptive()) return;
            var stack = new ItemStack(id, 1);
            if (!Inventory.CanFit(stack)) return;
            SrModels.Capture(actor.gameObject, id);
            var pos = actor.transform.position;
            Destroyer.DestroyActor(actor.gameObject, "SlimeCraft.Grab");
            Inventory.Add(stack);
            TutorialPatches.Vac(id);
            View.I?.Swing();
            Hud.ShowItemName();
            Sounds.Play("entity.item.pickup", pos, 0.2f, ((Random.value - Random.value) * 0.7f + 1f) * 2f);
            if (Identifiable.IsSlime(id)) Sounds.Play(Identifiable.IsLargo(id) ? "entity.slime.squish" : "entity.slime.squish_small", pos, 0.6f, Random.Range(0.9f, 1.3f));
        }

        /// <summary>Puts a held Slime Rancher actor down on the surface the player aims at, facing the player.</summary>
        private static void PutDown(ItemStack held, Vector3 point, Vector3 normal)
        {
            GameObject prefab = null;
            try { prefab = SRSingleton<GameContext>.Instance.LookupDirector.GetPrefab(held.Actor); } catch { }
            if (prefab == null) return;
            float r = PhysicsUtil.RadiusOfObject(prefab);
            var cam = Camera.main.transform;
            var face = Quaternion.LookRotation(Vector3.ProjectOnPlane(-cam.forward, Vector3.up).normalized + Vector3.forward * 0.001f, Vector3.up);
            var go = SrBridge.Spawn(held.Actor, point + normal * (r + 0.05f), face);
            if (go == null) return;
            if (!GameMode.Creative) Inventory.ShrinkHeld();
            TutorialPatches.Shoot(held.Actor);
            View.I?.Swing();
            Sounds.Play(Identifiable.IsSlime(held.Actor) ? "entity.slime.squish_small" : "block.grass.place", go.transform.position, 0.8f, Random.Range(0.9f, 1.1f));
        }

        /// <summary>Drops the food right in front of the slime's mouth; Slime Rancher's own appetite does the rest.</summary>
        private static void Feed(GameObject slime, ItemStack food)
        {
            var mouth = slime.transform.position + slime.transform.forward * 0.55f + Vector3.up * 0.25f;
            var go = SrBridge.Spawn(food.Actor, mouth, Quaternion.identity);
            if (go == null) return;
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null) rb.velocity = (slime.transform.position - mouth).normalized * 1.5f;
            if (!GameMode.Creative) Inventory.ShrinkHeld();
            View.I?.Swing();
            Sounds.Play("entity.item.pickup", mouth, 0.2f, 1.6f);
        }

        private static void StartUse(string action, int max)
        {
            Using = true; UseAction = action; UseMax = max; UseTicksLeft = max; UseElapsed = 0;
            if (action == "spear") Sounds.Play("item.trident.riptide_1", null, 0f); // preload
            View.I?.ForceRebuild();
        }

        public static void StopUsing(bool rebuild = true)
        {
            Using = false; UseAction = null;
            if (rebuild) View.I?.ForceRebuild();
        }

        private static void UseTick()
        {
            UseTicksLeft--; UseElapsed++;
            var held = Inventory.Held;
            if (held == null) { StopUsing(); return; }
            if (UseAction == "eat" || UseAction == "drink")
            {
                // LivingEntity.tickItemStackUsage: eating sounds and particles every 4 ticks after the first 7
                if (UseTicksLeft <= 25 && UseTicksLeft % 4 == 0)
                {
                    if (UseAction == "eat") { Sounds.Play("entity.generic.eat", SrWorld.Eye, 0.5f + 0.5f * Random.Range(0, 2), (Random.value - Random.value) * 0.2f + 1f); Particles.Eat(held); }
                    else Sounds.Play(held.Is("honey_bottle") ? "item.honey_bottle.drink" : "entity.generic.drink", SrWorld.Eye, 0.5f, Random.value * 0.1f + 0.9f);
                }
                if (UseTicksLeft <= 0) { FinishUse(held); StopUsing(); }
            }
            else if (UseAction == "crossbow")
            {
                float pull = CrossbowPullTime(held);
                if (UseElapsed == (int)(pull * 0.2f)) Sounds.Play("item.crossbow.loading_start", SrWorld.Eye);
                if (UseElapsed == (int)pull) Sounds.Play("item.crossbow.loading_end", SrWorld.Eye);
            }
        }

        private static void ReleaseUse()
        {
            var held = Inventory.Held;
            var action = UseAction;
            int used = UseElapsed;
            StopUsing();
            if (held == null) return;
            if (action == "bow") Projectiles.ShootBow(held, used);
            else if (action == "spear" && used >= 10) Projectiles.ThrowTrident(held);
            else if (action == "crossbow" && used >= CrossbowPullTime(held))
            {
                held.Potion = "charged";
                if (!GameMode.Creative)
                {
                    bool rocket = Inventory.CountOf(s => s.Is("firework_rocket")) > 0 && Inventory.Offhand != null && Inventory.Offhand.Is("firework_rocket");
                    Inventory.Remove(s => rocket ? s.Is("firework_rocket") : s.Is("arrow"), 1);
                    held.Flight = rocket ? 1 : 0;
                }
                View.I?.ForceRebuild();
            }
        }

        private static void ShootCrossbow(ItemStack held)
        {
            Projectiles.ShootCrossbow(held);
            held.Potion = null;
            held.Flight = 0;
            Inventory.DamageHeld(1);
            View.I?.ForceRebuild();
        }

        /// <summary>Item.finishUsing: food, drinks, potions, milk.</summary>
        private static void FinishUse(ItemStack held)
        {
            if (held.IsSr)
            {
                // A Slime Rancher fruit or veggie fills like a Minecraft apple or carrot.
                Hunger.Eat(4, 0.3f);
                Sounds.Play("entity.player.burp", SrWorld.Eye, 0.5f, Random.value * 0.1f + 0.9f);
                if (!GameMode.Creative) Inventory.ShrinkHeld();
                return;
            }
            var id = held.Item.Id;
            if (held.Item.Food != null)
            {
                var f = held.Item.Food;
                Hunger.Eat(f.Hunger, f.Saturation);
                foreach (var e in f.Effects) if (Random.value < e.Chance) Effects.Add(e.Effect, e.Duration, e.Amplifier);
                if (id == "chorus_fruit") Teleport.Random(8f);
                if (id == "honey_bottle") Effects.Remove("poison");
                Sounds.Play("entity.player.burp", SrWorld.Eye, 0.5f, Random.value * 0.1f + 0.9f);
                if (!GameMode.Creative)
                {
                    Inventory.ShrinkHeld();
                    if (id.EndsWith("_stew") || id.EndsWith("_soup")) Inventory.Add(ItemStack.Of("bowl"));
                    if (id == "honey_bottle") Inventory.Add(ItemStack.Of("glass_bottle"));
                }
                return;
            }
            if (id == "milk_bucket")
            {
                Effects.Clear();
                if (!GameMode.Creative) ReplaceHeld(ItemStack.Of("bucket"));
                return;
            }
            if (id == "potion")
            {
                if (held.Potion != null && McData.Potions.TryGetValue(held.Potion, out var list))
                    foreach (var e in list) Effects.Add(e.Effect, e.Duration, e.Amplifier);
                if (!GameMode.Creative) { Inventory.ShrinkHeld(); Inventory.Add(ItemStack.Of("glass_bottle")); }
            }
        }

        // ------------------------------------------------------------------------------------------------ misc

        /// <summary>A / Q drops one (Ctrl: the stack). Slime Rancher things come out as the real actor, thrown.</summary>
        public static void Drop(bool all)
        {
            var h = Inventory.Held;
            if (h == null) return;
            int n = all ? h.Count : 1;
            var one = h.Copy(n);
            h.Count -= n;
            if (h.Count <= 0) Inventory.Held = null;
            Throw(one);
            View.I?.Swing();
        }

        /// <summary>PlayerEntity.dropItem velocity: 0.3 along the look, slight random spread.</summary>
        public static void Throw(ItemStack s)
        {
            var cam = Camera.main.transform;
            var at = cam.position - Vector3.up * 0.3f;
            if (s.IsSr)
            {
                // 0.3 blocks per tick along the look, like an item toss; counts as shot by Beatrix (silos, market)
                var c = SrWorld.Controller;
                SrBridge.SpawnStack(s, at + cam.forward * 0.6f, cam.forward * 6f + Vector3.up * 2f + (c != null ? c.Velocity : Vector3.zero), true);
                TutorialPatches.Shoot(s.Actor);
                Sounds.Play("entity.item.pickup", at, 0.2f, 0.8f);
                return;
            }
            var look = cam.forward;
            float a = Random.value * Mathf.PI * 2f, f = 0.02f * Random.value;
            var vel = look * 0.3f + new Vector3(Mathf.Cos(a) * f, (Random.value - Random.value) * 0.1f + 0.1f, Mathf.Sin(a) * f);
            ItemEntity.Spawn(s, at, vel, 2f);
        }

        /// <summary>Middle click: in creative the block (or the targeted Slime Rancher thing) into the hand, in survival select it from the hotbar.</summary>
        private static void PickBlock()
        {
            var t = Current;
            ItemStack want = null;
            if (t.IsBlock) { var s = BlockWorld.Get(t.Cell); if (s?.Block.Item != null) want = new ItemStack(s.Block.Item); }
            else if (t.Mob != null) want = ItemStack.Of(t.Mob.Type + "_spawn_egg");
            else if (t.Actor != null) want = new ItemStack(t.Actor.id);
            if (want == null) return;
            for (int i = 0; i < 9; i++) if (Inventory.Main[i] != null && Inventory.Main[i].SameItem(want)) { Select(i); return; }
            if (!GameMode.Creative)
            {
                for (int i = 9; i < 36; i++)
                    if (Inventory.Main[i] != null && Inventory.Main[i].SameItem(want))
                    {
                        var tmp = Inventory.Main[i]; Inventory.Main[i] = Inventory.Held; Inventory.Held = tmp; return;
                    }
                return;
            }
            int slot = Inventory.Held == null ? Inventory.Selected : System.Array.FindIndex(Inventory.Main, 0, 9, x => x == null);
            if (slot < 0) slot = Inventory.Selected;
            Select(slot);
            want.Count = want.MaxStack;
            Inventory.Held = want;
        }

        private static void SwapHands()
        {
            var a = Inventory.Held; Inventory.Held = Inventory.Offhand; Inventory.Offhand = a;
        }

        public static void TestAttack() { Current = Raycast(); if (Current.Any) Attack(Current); }
        public static void TestUse() { Current = Raycast(); Use(Current, true); }
    }

    /// <summary>Kept for the engine's tick list; Slime Rancher has no loose resources to sweep up.</summary>
    public static class Pickups
    {
        public static void Update() { }
    }
}
