using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>What the crosshair is on: a Minecraft block, a Minecraft mob, a Subnautica creature, object or terrain.</summary>
    public struct Target
    {
        public bool Any;
        public bool IsBlock;
        public Vector3Int Cell;
        public Dir Face;
        public Vector3 Point, Normal;
        public float Distance;
        public Mob Mob;
        public LiveMixin Live;          // a Subnautica creature (or anything with health)
        public GameObject SnObject;     // Subnautica object hit
        public bool Terrain;
        public RaycastHit Hit;
    }

    /// <summary>
    /// Minecraft's controls on top of Subnautica: left click attacks (PlayerEntity.attack with the cooldown,
    /// critical hits, sweeping, Sharpness/Impaling/Knockback/Fire Aspect) and mines (Minecraft blocks with their
    /// real hardness and the cracking overlay; Subnautica's ground and outcrops give Minecraft blocks and their own
    /// resources), right click uses: Subnautica's buttons, doors, fabricators and pickups, Minecraft blocks
    /// (doors, chests, crafting tables...), placing blocks, eating, bows, tridents (with Riptide in the ocean),
    /// crossbows, buckets, fireworks, armour, spawn eggs, throwables, fishing. Q drops, middle click picks a block.
    /// A held Subnautica tool keeps Subnautica's own controls.
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
        public static bool SuppressSnRight, SuppressSnLeft;

        // mining
        private static Vector3Int miningCell; private static bool mining, miningTerrain;
        private static float progress; private static int hitSoundTicks;
        private static BlockDef terrainBlock; private static bool terrainOre; private static Vector3 terrainPoint, terrainNormal;
        private static BreakableResource miningOutcrop;
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
            if (crack != null) Object.Destroy(crack);
        }

        // ------------------------------------------------------------------------------------------------ targeting

        private static Target Raycast()
        {
            var t = new Target();
            var cam = MainCamera.camera.transform;
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
            // 3. Subnautica's world
            int mask = ~((1 << LayerID.Player) | (1 << LayerID.Trigger) | (1 << LayerID.UI) | (1 << View.HandLayer));
            var hits = Physics.RaycastAll(origin, dir, reach, mask, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                if (h.collider == null || BlockWorld.CellOf(h.collider, out _)) continue;
                if (h.collider.transform.IsChildOf(Player.main.transform)) continue;
                if (h.collider.GetComponentInParent<Mob>() != null) continue;
                if (View.I != null && View.I.Body != null && h.collider.transform.IsChildOf(View.I.Body.transform)) continue;
                var tool = SrBridge.HeldTool;
                if (tool != null && h.collider.transform.IsChildOf(tool.transform)) continue;
                t = new Target { Any = true, Hit = h, Point = h.point, Normal = h.normal, Distance = h.distance, SnObject = h.collider.gameObject };
                var lm = h.collider.GetComponentInParent<LiveMixin>();
                if (lm != null && lm.IsAlive() && lm.GetComponent<Creature>() != null) t.Live = lm;
                t.Terrain = SrWorld.IsTerrain(h.collider);
                break;
            }
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
            var p = Player.main;
            if (p == null || MainCamera.camera == null) return;
            bool blocked = Screens.Open || Chat.Open || Motor.SuspendedByGame || p.GetPDA() != null && p.GetPDA().isInUse || !AvatarInputHandler.main.IsEnabled() || Time.timeScale == 0;
            SuppressSnLeft = SuppressSnRight = false;
            Current = blocked ? new Target() : Raycast();

            bool left = !blocked && Input.GetMouseButton(0), right = !blocked && Input.GetMouseButton(1);
            // a click is kept until a 20 Hz tick handles it, so a quick click between two ticks is not lost
            if (left && !leftWas) leftLatch = true;
            if (right && !rightWas) rightLatch = true;
            if (blocked) { leftLatch = rightLatch = false; }
            bool leftDown = leftLatch, rightDown = rightLatch;
            leftWas = left; rightWas = right;

            var held = Inventory.Held;
            bool snTool = held != null && held.IsSr && SrItems.IsTool(held.Actor) && SrBridge.HeldTool != null;
            var hand = p.guiHand;
            var snTarget = hand != null ? hand.GetActiveTarget() : null;

            if (!blocked)
            {
                // hotbar: number keys and the wheel (Minecraft's scroll goes the other way round from Subnautica's)
                for (int i = 0; i < 9; i++) if (Input.GetKeyDown(KeyCode.Alpha1 + i)) Select(i);
                float wheel = Input.mouseScrollDelta.y;
                if (wheel > 0.01f) Select((Inventory.Selected + 8) % 9);
                else if (wheel < -0.01f) Select((Inventory.Selected + 1) % 9);
                if (Input.GetKeyDown(Game.DropKey)) Drop(Input.GetKey(KeyCode.LeftControl));
                if (Input.GetMouseButtonDown(2)) PickBlock();
                if (Input.GetKeyDown(KeyCode.F) && !snTool) SwapHands();
            }

            // Subnautica tool in hand: its own controls; right click still uses what is targeted
            if (snTool)
            {
                if (rightDown && snTarget != null && !SrBridge.HeldTool.DoesOverrideHand())
                {
                    GUIHand.Send(snTarget, HandTargetEventType.Click, hand);
                    SuppressSnRight = true;
                    View.I?.Swing();
                }
                if (leftDown) View.I?.Swing();
                leftLatch = rightLatch = false;
                StopMining();
                return;
            }

            tickAcc += Time.deltaTime;
            int guard = 0;
            while (tickAcc >= 0.05f && guard++ < 4) { tickAcc -= 0.05f; Tick(left || leftDown, right || rightDown, leftDown, rightDown, snTarget, hand); leftDown = false; rightDown = false; leftLatch = rightLatch = false; }
            UpdateCrack();
        }

        private static void Select(int i)
        {
            if (i == Inventory.Selected) return;
            Inventory.Selected = i;
            StopUsing(false);
            Hud.ShowItemName();
        }

        private static void Tick(bool left, bool right, bool leftDown, bool rightDown, GameObject snTarget, GUIHand hand)
        {
            if (rightDelay > 0) rightDelay--;
            if (breakCooldown > 0) breakCooldown--;
            var t = Current;
            var held = Inventory.Held;

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
                    if (t.Mob != null) { Attack(t); return; }
                    if (t.Live != null) { Attack(t); return; }
                    // Subnautica's things answer the left click too (hatches, buttons, pickups), as long as it is not
                    // something to fight or to mine
                    if (snTarget != null && !t.IsBlock && snTarget.GetComponentInParent<BreakableResource>() == null)
                    {
                        var lm = snTarget.GetComponentInParent<LiveMixin>();
                        bool creature = lm != null && lm.IsAlive() && snTarget.GetComponentInParent<Creature>() != null;
                        if (!creature)
                        {
                            GUIHand.Send(snTarget, HandTargetEventType.Click, hand);
                            View.I?.Swing();
                            return;
                        }
                    }
                    if (!t.Any) { View.I?.Swing(); View.I?.ResetAttackCooldown(); }
                }
                if (t.IsBlock || t.Terrain || t.SnObject != null && t.SnObject.GetComponentInParent<BreakableResource>() != null || t.SnObject != null && Utils.GetObjectSurfaceType(t.SnObject) != VFXSurfaceTypes.none && !t.Live)
                    Mine(t);
                else StopMining();
            }
            else StopMining();

            // ---- right: use
            if (right && rightDelay == 0)
            {
                rightDelay = 4; // Minecraft's itemUseCooldown
                Use(t, snTarget, hand, rightDown);
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
            bool undead = t.Mob != null && t.Mob.Undead, arthropod = t.Mob != null && t.Mob.Arthropod, aquatic = t.Mob != null ? t.Mob.Aquatic : t.Live != null;
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
            var yaw = MainCamera.camera.transform.forward; yaw.y = 0; yaw.Normalize();

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
            if (t.Live != null)
            {
                // Subnautica creatures: one Minecraft health point is 5 of theirs, like the player's own scale
                bool ok = t.Live.TakeDamage(dmg * Health.Scale, t.Point, fire > 0 ? DamageType.Fire : DamageType.Normal, Player.main.gameObject);
                var rb = t.Live.GetComponent<Rigidbody>();
                if (rb != null && !rb.isKinematic) rb.AddForce((dir * (0.4f + knock * 0.5f) + Vector3.up * 0.1f) * 8f, ForceMode.VelocityChange);
                if (!t.Live.IsAlive()) OnSnKill(t.Live);
                return ok || dmg > 0;
            }
            return false;
        }

        /// <summary>Killing a Subnautica creature gives Minecraft experience by its size, like a mob kill.</summary>
        private static void OnSnKill(LiveMixin lm)
        {
            int xp = Mathf.Clamp(Mathf.RoundToInt(lm.maxHealth / 20f), 1, 50);
            XpOrb.Spawn(lm.transform.position, xp);
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
                if (!mining || miningTerrain || miningCell != t.Cell) { mining = true; miningTerrain = false; miningCell = t.Cell; progress = 0; hitSoundTicks = 0; miningOutcrop = null; }
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
            // Subnautica: an outcrop breaks into its resources; the ground gives Minecraft blocks
            var outcrop = t.SnObject != null ? t.SnObject.GetComponentInParent<BreakableResource>() : null;
            if (!mining || !miningTerrain || (terrainPoint - t.Point).sqrMagnitude > 0.5f || miningOutcrop != outcrop)
            {
                mining = true; miningTerrain = true; progress = 0; hitSoundTicks = 0;
                terrainPoint = t.Point; terrainNormal = t.Normal; miningOutcrop = outcrop;
                terrainBlock = outcrop != null ? McData.Block("stone") : SrWorld.MinedBlock(t.Hit, out terrainOre);
            }
            if (terrainBlock == null) return;
            View.I?.Swing();
            float delta = GameMode.Creative ? 1f : Mining.Delta(terrainBlock, held);
            if (outcrop != null) delta = Mathf.Max(delta, Mining.BreakingSpeed(terrainBlock, held) / 1.5f / 30f); // outcrops break even by hand
            progress += delta;
            if (hitSoundTicks++ % 4 == 0) Sounds.Play(Sounds.BlockEvent(terrainBlock.Sound, "hit"), t.Point, 0.25f, 0.5f);
            Particles.Hit(t.Point, t.Normal, terrainBlock.Default);
            if (progress >= 1f)
            {
                Sounds.Play(Sounds.BlockEvent(terrainBlock.Sound, "break"), t.Point, 1f, 0.8f);
                Particles.Break(t.Point - t.Normal * 0.5f, terrainBlock.Default);
                if (outcrop != null)
                {
                    outcrop.BreakIntoResources();
                    Pickups.CollectSoon(t.Point, 3f);
                }
                else if (!GameMode.Creative)
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

        private static void Use(Target t, GameObject snTarget, GUIHand hand, bool fresh)
        {
            var held = Inventory.Held;
            bool sneaking = Motor.Sneaking;

            // 1. Minecraft block interaction (doors, chests, crafting tables...) unless sneaking with an item
            if (t.IsBlock && (!sneaking || held == null))
            {
                var s = BlockWorld.Get(t.Cell);
                if (s != null && BlockRules.Use(t.Cell, s)) { View.I?.Swing(); return; }
            }
            // 2. Subnautica's things under the crosshair (buttons, doors, fabricators, lockers, beds, pickups)
            if (snTarget != null && !t.IsBlock && (held == null || !held.IsMc || held.Item.Block == null || !sneaking))
            {
                if (fresh) { GUIHand.Send(snTarget, HandTargetEventType.Click, hand); View.I?.Swing(); }
                return;
            }
            // 3. Minecraft mob interaction (buckets on fish, shears...)
            if (t.Mob != null && held != null && t.Mob.Interact(held)) { View.I?.Swing(); return; }
            if (held == null) return;

            // 4. the item itself
            if (held.IsSr) { UseSnItem(held); return; }
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
                        Sounds.Play("entity.firework_rocket.launch", Player.main.transform.position, 3f);
                        Inventory.ShrinkHeld();
                        Particles.Rocket(Player.main.transform.position);
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
                        Sounds.Play("item.bucket.fill", Player.main.transform.position);
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
                        Sounds.Play("item.bottle.fill", Player.main.transform.position);
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
                // on Subnautica geometry: the cell just off the surface, on the side the ray came from
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
            var ctx = new PlaceCtx { Cell = cell, Side = side, Hit = t.Point, Look = BlockRules.McLook(MainCamera.camera.transform.forward), Sneaking = Motor.Sneaking };
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
                    if (c.transform.IsChildOf(Player.main.transform) || c.GetComponentInParent<Creature>() != null || c.GetComponentInParent<Mob>() != null || c.GetComponentInParent<Vehicle>() != null) return;
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

        private static void UseSnItem(ItemStack held)
        {
            if (!Hunger.CanEat(true)) return;
            var info = SrFood.Get(held.Actor);
            if (info == null) return;
            if (info.Food <= 0 && info.Water <= 0) return;
            StartUse(info.Food <= 0 ? "drink" : "eat", 32);
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
                    if (UseAction == "eat") { Sounds.Play("entity.generic.eat", Player.main.transform.position, 0.5f + 0.5f * Random.Range(0, 2), (Random.value - Random.value) * 0.2f + 1f); Particles.Eat(held); }
                    else Sounds.Play(held.Is("honey_bottle") ? "item.honey_bottle.drink" : "entity.generic.drink", Player.main.transform.position, 0.5f, Random.value * 0.1f + 0.9f);
                }
                if (UseTicksLeft <= 0) { FinishUse(held); StopUsing(); }
            }
            else if (UseAction == "crossbow")
            {
                float pull = CrossbowPullTime(held);
                if (UseElapsed == (int)(pull * 0.2f)) Sounds.Play("item.crossbow.loading_start", Player.main.transform.position);
                if (UseElapsed == (int)pull) Sounds.Play("item.crossbow.loading_end", Player.main.transform.position);
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
                var info = SrFood.Get(held.Actor);
                if (info != null)
                {
                    var sv = Player.main.GetComponent<Survival>();
                    sv.food = Mathf.Clamp(sv.food + info.Food, 0, 200f);
                    sv.water = Mathf.Clamp(sv.water + info.Water, 0, 100f);
                    Hunger.Saturation = Mathf.Min(Hunger.Saturation + Mathf.Max(0, info.Food) / 5f * 0.6f * 2f, Hunger.Food);
                    if (info.Health > 0) Health.Heal(info.Health / Health.Scale);
                    Sounds.Play("entity.player.burp", Player.main.transform.position, 0.5f, Random.value * 0.1f + 0.9f);
                    if (!GameMode.Creative) Inventory.ShrinkHeld();
                    if (held.Actor == Identifiable.Id.FilteredWater || held.Actor == Identifiable.Id.BigFilteredWater || held.Actor == Identifiable.Id.DisinfectedWater) { }
                }
                return;
            }
            var id = held.Item.Id;
            if (held.Item.Food != null)
            {
                var f = held.Item.Food;
                Hunger.Eat(f.Hunger, f.Saturation);
                // Minecraft foods with water in them help Subnautica's thirst a little
                if (id == "melon_slice" || id == "sweet_berries" || id == "glow_berries" || id == "apple" || id.EndsWith("_stew") || id.EndsWith("_soup")) Hunger.Drink(id.EndsWith("_stew") || id.EndsWith("_soup") ? 15f : 5f);
                foreach (var e in f.Effects) if (Random.value < e.Chance) Effects.Add(e.Effect, e.Duration, e.Amplifier);
                if (id == "chorus_fruit") Teleport.Random(Player.main, 8f);
                if (id == "honey_bottle") Effects.Remove("poison");
                Sounds.Play("entity.player.burp", Player.main.transform.position, 0.5f, Random.value * 0.1f + 0.9f);
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
                if (held.Potion == "water") Hunger.Drink(30f); // a bottle of water quenches Subnautica's thirst
                if (held.Potion != null && McData.Potions.TryGetValue(held.Potion, out var list))
                    foreach (var e in list) Effects.Add(e.Effect, e.Duration, e.Amplifier);
                if (!GameMode.Creative) { Inventory.ShrinkHeld(); Inventory.Add(ItemStack.Of("glass_bottle")); }
            }
        }

        // ------------------------------------------------------------------------------------------------ misc

        /// <summary>Q / A drops one (Ctrl: the stack). Subnautica items come out as the real thing (a fish swims off).</summary>
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
            var cam = MainCamera.camera.transform;
            var at = cam.position - Vector3.up * 0.3f;
            if (s.IsSr)
            {
                if (!SrBridge.IsVirtual(s.Actor)) { SrBridge.TakeReal(s.Actor, s.Count, false); return; }
                UWE.CoroutineHost.StartCoroutine(SpawnSn(s.Actor, s.Count, at + cam.forward * 0.5f, cam.forward * 3f));
                return;
            }
            var look = cam.forward;
            float a = Random.value * Mathf.PI * 2f, f = 0.02f * Random.value;
            var vel = look * 0.3f + new Vector3(Mathf.Cos(a) * f, (Random.value - Random.value) * 0.1f + 0.1f, Mathf.Sin(a) * f);
            ItemEntity.Spawn(s, at, vel, 2f);
        }

        public static System.Collections.IEnumerator SpawnSn(Identifiable.Id t, int n, Vector3 at, Vector3 vel)
        {
            for (int i = 0; i < n; i++)
            {
                var task = CraftData.GetPrefabForTechTypeAsync(t);
                yield return task;
                var prefab = task.GetResult();
                if (prefab == null) yield break;
                var go = Object.Instantiate(prefab, at + Random.insideUnitSphere * 0.2f, Quaternion.identity);
                go.SetActive(true);
                var rb = go.GetComponent<Rigidbody>();
                if (rb != null) { rb.isKinematic = false; rb.velocity = vel; }
                var lwe = go.GetComponent<LargeWorldEntity>();
                if (lwe != null && LargeWorld.main != null) LargeWorld.main.streamer.cellManager.RegisterEntity(lwe);
            }
        }

        /// <summary>Middle click: in creative the block (or the targeted Subnautica thing) into the hand, in survival select it from the hotbar.</summary>
        private static void PickBlock()
        {
            var t = Current;
            ItemStack want = null;
            if (t.IsBlock) { var s = BlockWorld.Get(t.Cell); if (s?.Block.Item != null) want = new ItemStack(s.Block.Item); }
            else if (t.Mob != null) want = ItemStack.Of(t.Mob.Type + "_spawn_egg");
            else if (t.SnObject != null) { var tt = CraftData.GetTechType(t.SnObject); if (tt != Identifiable.Id.NONE) want = new ItemStack(tt); }
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
            if (want.IsSr) { Inventory.Held = null; if (!SrBridge.IsVirtual(want.Actor)) { SrBridge.GiveReal(want.Actor, 1); return; } }
            Inventory.Held = want;
        }

        private static void SwapHands()
        {
            var a = Inventory.Held; Inventory.Held = Inventory.Offhand; Inventory.Offhand = a;
        }

        public static void TestAttack() { if (Current.Any) Attack(Current); }
    }

    /// <summary>Subnautica's left / right hand inputs are ours unless a Subnautica tool is held.</summary>
    [HarmonyPatch]
    internal static class HandInputPatches
    {
        [HarmonyPatch(typeof(GUIHand), "GetInput"), HarmonyPrefix]
        private static bool Input(GameInput.Button button, ref bool __result)
        {
            if (!Game.Active || Motor.SuspendedByGame) return true;
            if (button != GameInput.Button.LeftHand && button != GameInput.Button.RightHand && button != GameInput.Button.AltTool) return true;
            var held = Inventory.Held;
            bool snTool = held != null && held.IsSr && SrItems.IsTool(held.Actor) && SrBridge.HeldTool != null;
            if (snTool && !(button == GameInput.Button.RightHand && Interaction.SuppressSnRight)) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>Food values of Subnautica items (from their prefab's Eatable), fetched once.</summary>
    public static class SrFood
    {
        public class Info { public float Food, Water, Health; }
        private static readonly Dictionary<Identifiable.Id, Info> cache = new Dictionary<Identifiable.Id, Info>();
        private static readonly HashSet<Identifiable.Id> loading = new HashSet<Identifiable.Id>();

        public static Info Get(Identifiable.Id t)
        {
            if (cache.TryGetValue(t, out var i)) return i;
            if (loading.Add(t)) UWE.CoroutineHost.StartCoroutine(Load(t));
            return null;
        }

        public static void Prefetch(Identifiable.Id t) => Get(t);

        private static System.Collections.IEnumerator Load(Identifiable.Id t)
        {
            var task = CraftData.GetPrefabForTechTypeAsync(t, false);
            yield return task;
            var prefab = task.GetResult();
            Info info = null;
            if (prefab != null)
            {
                var e = prefab.GetComponent<Eatable>();
                if (e != null) info = new Info { Food = e.foodValue, Water = e.waterValue };
                if (t == Identifiable.Id.FirstAidKit) info = new Info { Health = 50f };
            }
            cache[t] = info;
        }
    }

    /// <summary>Picks up Subnautica resources that just fell out of a broken outcrop when the player walks over them.</summary>
    public static class Pickups
    {
        private static readonly List<KeyValuePair<Vector3, float>> zones = new List<KeyValuePair<Vector3, float>>();
        private static readonly HashSet<Pickupable> known = new HashSet<Pickupable>();

        public static void CollectSoon(Vector3 at, float radius) => zones.Add(new KeyValuePair<Vector3, float>(at, Time.time + 15f));

        public static void Update()
        {
            if (zones.Count == 0 || Player.main == null) return;
            zones.RemoveAll(z => Time.time > z.Value);
            var feet = Player.main.transform.position - Vector3.up * Motor.EyeHeight;
            foreach (var z in zones)
            {
                foreach (var c in Physics.OverlapSphere(z.Key, 3f, ~0, QueryTriggerInteraction.Collide))
                {
                    var pk = c.GetComponentInParent<Pickupable>();
                    if (pk == null || !pk.isPickupable || !pk.gameObject.activeInHierarchy) continue;
                    var lm = pk.GetComponent<LiveMixin>();
                    if (lm != null && lm.IsAlive()) continue;
                    var d = pk.transform.position - (feet + Vector3.up * 0.9f);
                    if (Mathf.Abs(d.x) < 1.3f && Mathf.Abs(d.z) < 1.3f && d.y > -1.4f && d.y < 1.4f) global::Inventory.main.Pickup(pk);
                }
            }
        }
    }
}
