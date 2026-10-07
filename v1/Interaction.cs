using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// What the mouse does, Minecraft style, in Slime Rancher's world:
    ///   left  - punch slimes back (and pop tarrs), dig Minecraft blocks with cracks and particles
    ///   right - pick up a slime/plort/food/chicken into the inventory, put the held one back down, feed the
    ///           slime you look at, place blocks, eat food in the air, and use Slime Rancher's own buttons
    ///   middle - pick block; A/Q drops (Ctrl for the stack); 1-9 and the wheel choose the hotbar slot
    /// </summary>
    public static class Interaction
    {
        public const float Reach = 4.5f;
        // Slime Rancher's solid world, plus every actor layer: Actor, Launched, ActorStatic, and ActorIgnorePlayer
        // (plorts, fruits and veggies lying around, which the player walks through) and ActorEchoes.
        private const int Mask = -675375893 | (1 << 15) | (1 << 9) | (1 << 21) | (1 << 0) | (1 << 11) | (1 << 16);

        public struct Target
        {
            public bool Any;
            public RaycastHit Hit;
            public BlockTag Block;
            public Identifiable Actor;
        }

        public static Target Current;
        public static bool UiTargeted;          // Slime Rancher's UIDetector shows a prompt: right-click activates it
        private static int useCooldown;
        private static Vector3Int breaking = new Vector3Int(int.MinValue, 0, 0);
        private static float breakProgress, prevBreakProgress;
        private static int breakSoundTick;
        private static readonly Dictionary<int, int> tarrHits = new Dictionary<int, int>();
        private static float tickAcc;
        private static GameObject outline, crack;
        private static MeshRenderer crackRenderer;
        private static Material[] crackMats;
        private static bool rightWasDown;

        public static bool AzertyKeyboard;

        public static void Reset()
        {
            breaking = new Vector3Int(int.MinValue, 0, 0);
            breakProgress = prevBreakProgress = 0;
            tarrHits.Clear();
            if (outline != null) Object.Destroy(outline);
            if (crack != null) Object.Destroy(crack);
        }

        // ------------------------------------------------------------------------------------------------ targeting

        public static void UpdateTarget(Camera cam)
        {
            Current = default;
            if (cam == null) return;
            var player = SRSingleton<SceneContext>.Instance.Player;
            var ray = new Ray(cam.transform.position, cam.transform.forward);
            if (View.I != null && View.I.Perspective != 0)
            {
                // In third person, aim from the eyes along the look direction, like Minecraft.
                var c = player.GetComponent<vp_FPController>();
                var eye = c.SmoothPosition + Vector3.up * Motor.EyeHeight;
                var dir = View.I.Perspective == 2 ? -cam.transform.forward : cam.transform.forward;
                ray = new Ray(eye, dir);
            }
            var hits = Physics.RaycastAll(ray, Reach, Mask, QueryTriggerInteraction.Collide);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            Target first = default;
            foreach (var h in hits)
            {
                var col = h.collider;
                if (col == null) continue;
                if (col.transform.IsChildOf(player.transform)) continue;
                var tag = col.GetComponent<BlockTag>();
                if (tag != null) { first = new Target { Any = true, Hit = h, Block = tag }; break; }
                var id = Grabbable(col);
                if (id != null) { first = new Target { Any = true, Hit = h, Actor = id }; break; }
                if (col.isTrigger) continue;
                first = new Target { Any = true, Hit = h };
                break;
            }
            // Minecraft entity hitboxes are generous; a fruit hanging in a tree or a carrot in the soil sits half
            // inside other colliders, so also sweep a small sphere and take any grabbable thing about as close.
            if (first.Actor == null)
            {
                float limit = first.Any ? first.Hit.distance + 0.35f : Reach;
                foreach (var h in Physics.SphereCastAll(ray, 0.3f, Mathf.Min(limit, Reach), Mask, QueryTriggerInteraction.Collide))
                {
                    if (h.collider == null || h.collider.transform.IsChildOf(player.transform)) continue;
                    var id = Grabbable(h.collider);
                    if (id == null) continue;
                    if (first.Actor == null || h.distance < first.Hit.distance)
                    {
                        var hit = h;
                        if (hit.point == Vector3.zero) hit.point = id.transform.position;
                        first = new Target { Any = true, Hit = hit, Actor = id };
                    }
                }
            }
            Current = first;
        }

        /// <summary>A Slime Rancher thing Steve can grab: anything the vacpack could take right now.</summary>
        private static Identifiable Grabbable(Collider col)
        {
            var id = col.GetComponentInParent<Identifiable>();
            if (id == null) return null;
            var vac = id.GetComponent<Vacuumable>();
            if (vac == null || !vac.enabled) return null;
            return id;
        }

        // ------------------------------------------------------------------------------------------------ frame

        public static void Update(Camera cam)
        {
            if (Screens.Open || Time.timeScale == 0f || SRInput.Instance.GetInputMode() != SRInput.InputMode.DEFAULT)
            {
                ShowOutline(false);
                rightWasDown = Input.GetMouseButton(1);
                return;
            }
            UpdateTarget(cam);
            HotbarKeys();

            bool left = Input.GetMouseButton(0), leftDown = Input.GetMouseButtonDown(0);
            bool right = Input.GetMouseButton(1), rightDown = Input.GetMouseButtonDown(1) || (right && !rightWasDown);
            rightWasDown = right;

            // Slime Rancher's own buttons, doors, plot screens... take the right click when one is under the cursor.
            if (rightDown && UiTargeted) { Patches.RequestInteract(); useCooldown = 4; View.I?.Swing(); return; }

            if (leftDown) Attack();
            if (Input.GetMouseButtonDown(2)) PickBlock();
            if (Input.GetKeyDown(DropKey)) Drop(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl));
            if (!right && View.I != null && View.I.UseTicksLeft > 0) View.I.UseTicksLeft = 0;

            tickAcc += Time.deltaTime;
            int guard = 0;
            while (tickAcc >= 0.05f && guard++ < 5)
            {
                tickAcc -= 0.05f;
                if (useCooldown > 0) useCooldown--;
                DigTick(left);
                if (right && (rightDown || useCooldown == 0) && (View.I == null || View.I.UseTicksLeft == 0)) { Use(); rightDown = false; }
            }
            DrawOutline();
        }

        public static void TestUse() { var cam = Camera.main; UpdateTarget(cam); Use(); }
        public static void TestAttack() { var cam = Camera.main; UpdateTarget(cam); Attack(); }
        public static void TestDig(int ticks) { var cam = Camera.main; UpdateTarget(cam); for (int i = 0; i < ticks; i++) DigTick(true); }

        public static KeyCode DropKey => AzertyKeyboard ? KeyCode.A : KeyCode.Q;

        private static void HotbarKeys()
        {
            for (int i = 0; i < 9; i++)
                if (Input.GetKeyDown(KeyCode.Alpha1 + i)) Select(i);
            float wheel = Input.mouseScrollDelta.y;
            if (wheel > 0.1f) Select((Inventory.Selected + 8) % 9);
            else if (wheel < -0.1f) Select((Inventory.Selected + 1) % 9);
        }

        private static void Select(int i)
        {
            if (Inventory.Selected == i) return;
            Inventory.Selected = i;
            Hud.ShowItemName();
            if (View.I != null) View.I.UseTicksLeft = 0;
        }

        // ------------------------------------------------------------------------------------------------ left

        private static void Attack()
        {
            View.I?.Swing();
            var t = Current;
            if (t.Actor != null)
            {
                float strength = View.I != null ? View.I.Cooldown(0.5f) : 1f;
                View.I?.ResetAttackCooldown();
                var rb = t.Actor.GetComponent<Rigidbody>();
                var cam = Camera.main.transform;
                var dir = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
                if (rb != null && !rb.isKinematic)
                    rb.AddForce(dir * (5f * strength) + Vector3.up * (3f * strength), ForceMode.VelocityChange);
                McSound.Play(strength > 0.9f ? "entity/player/attack/knockback" : "entity/player/attack/weak", t.Hit.point, 1f, 1f);
                if (Identifiable.IsSlime(t.Actor.id)) McSound.Play("mob/slime/small", t.Hit.point, 0.8f, Random.Range(0.8f, 1.2f));
                // Tarrs have no water gun to fear any more: three full-strength punches pop them.
                if (Identifiable.IsTarr(t.Actor.id) && strength > 0.9f)
                {
                    int key = t.Actor.gameObject.GetInstanceID();
                    tarrHits.TryGetValue(key, out int n);
                    if (++n >= 3) { tarrHits.Remove(key); Pop(t.Actor.gameObject); }
                    else tarrHits[key] = n;
                }
                return;
            }
            if (t.Block == null) View.I?.ResetAttackCooldown();
        }

        private static void Pop(GameObject go)
        {
            var pos = go.transform.position;
            Particles.Burst(pos, null, new Color(0.15f, 0.1f, 0.2f), 20);
            McSound.Play("mob/slime/big", pos, 1f, 0.8f);
            Destroyer.DestroyActor(go, "SlimeCraft.Punch");
        }

        private static int creativeBreakCooldown;

        private static void DigTick(bool left)
        {
            prevBreakProgress = breakProgress;
            if (creativeBreakCooldown > 0) creativeBreakCooldown--;
            if (!left || Current.Block == null)
            {
                breaking = new Vector3Int(int.MinValue, 0, 0);
                breakProgress = 0;
                return;
            }
            var cell = Current.Block.Cell;
            if (!Blocks.World.TryGetValue(cell, out var p)) return;
            View.I?.Swing();
            if (GameMode.Creative)
            {
                // Creative: gone at once, one block every 5 ticks while the button is held, nothing drops.
                if (creativeBreakCooldown > 0) return;
                creativeBreakCooldown = 5;
                Break(p, false);
                return;
            }
            if (cell != breaking) { breaking = cell; breakProgress = 0; breakSoundTick = 0; }
            // Dig time as with the right Minecraft tool (iron tier, speed 6), capped so nothing takes forever by hand.
            float seconds = Mathf.Clamp(p.Def.Hardness * 1.5f / 6f, 0.05f, 2.5f);
            breakProgress += 0.05f / seconds;
            if (breakSoundTick++ % 4 == 0) McSound.Play(Blocks.HitSound(p.Def), Blocks.Center(cell), 0.25f, 0.5f);
            if (breakProgress >= 1f)
            {
                Break(p, true);
                breakProgress = 0;
                breaking = new Vector3Int(int.MinValue, 0, 0);
            }
        }

        private static void Break(Blocks.Placed p, bool drops)
        {
            var cell = p.Cell;
            var center = Blocks.Center(cell);
            Blocks.Remove(cell);
            Screens.ContainerGone(p);
            Particles.Burst(center, p.Def, Color.white, 24);
            if (p.Def.Shape == BlockShape.Shulker)
            {
                // A shulker box keeps what it holds, even in creative, exactly like Minecraft.
                var box = new ItemStack(p.Def, 1);
                if (p.Contents != null) box.Contents = p.Contents;
                if (drops || box.HasContents) ItemEntity.Spawn(box, center, new Vector3(Random.Range(-1f, 1f), 2f, Random.Range(-1f, 1f)));
                return;
            }
            if (p.Contents != null)
                foreach (var c in p.Contents) if (c != null) DropAt(c, center);
            int count = p.Def.Shape == BlockShape.Slab && p.Rot == 2 ? 2 : 1;
            if (drops) ItemEntity.Spawn(new ItemStack(p.Def, count), center, new Vector3(Random.Range(-1f, 1f), 2f, Random.Range(-1f, 1f)));
        }

        /// <summary>Spills a stack into the world: blocks as Minecraft items, Slime Rancher things as the real actors.</summary>
        public static void DropAt(ItemStack s, Vector3 at)
        {
            if (s.IsBlock) { ItemEntity.Spawn(s, at, new Vector3(Random.Range(-1.5f, 1.5f), 2.5f, Random.Range(-1.5f, 1.5f))); return; }
            for (int i = 0; i < s.Count; i++)
            {
                var go = Spawn(s.Actor, at + Random.insideUnitSphere * 0.3f + Vector3.up * 0.3f, Quaternion.identity);
                if (go == null) continue;
                PhysicsUtil.RestoreFreezeRotationConstraints(go);
                var rb = go.GetComponent<Rigidbody>();
                if (rb != null) rb.velocity = new Vector3(Random.Range(-1.5f, 1.5f), 3f, Random.Range(-1.5f, 1.5f));
            }
        }

        // ------------------------------------------------------------------------------------------------ right

        private static void Use()
        {
            useCooldown = 4;
            if (Current.Any && Current.Hit.collider != null)
                Plugin.Log.LogInfo("use -> " + (Current.Actor != null ? "actor " + Current.Actor.id : Current.Block != null ? "block" : Current.Hit.collider.name + " L" + Current.Hit.collider.gameObject.layer));
            var held = Inventory.Held;
            var t = Current;

            if (t.Actor != null)
            {
                bool slime = Identifiable.IsSlime(t.Actor.id) && !Identifiable.IsTarr(t.Actor.id);
                bool sneak = Motor.Sneaking;
                if (held != null && held.IsActor && slime && !sneak && IsFoodFor(held.Actor))
                {
                    Feed(t.Actor.gameObject, held);
                    return;
                }
                PickUp(t.Actor);
                return;
            }

            if (t.Block != null && !Motor.Sneaking && Blocks.World.TryGetValue(t.Block.Cell, out var target) && target.Def.IsContainer)
            {
                Screens.OpenContainer(target);
                return;
            }

            if (held == null) return;

            if (t.Any && t.Hit.distance <= Reach)
            {
                if (held.IsBlock) { PlaceBlock(held, t); return; }
                PlaceActor(held, t.Hit.point, t.Hit.normal);
                return;
            }

            // Nothing in reach: eat it, if it is something Steve can eat.
            if (held.IsActor && (Identifiable.IsFruit(held.Actor) || Identifiable.IsVeggie(held.Actor)) && View.I != null)
                View.I.UseTicksLeft = 32;
        }

        private static bool IsFoodFor(Identifiable.Id food)
        {
            return Identifiable.IsFood(food) || Identifiable.IsPlort(food) || Identifiable.IsChick(food) || Identifiable.IsAnimal(food);
        }

        public static void EatTick(int left)
        {
            var held = Inventory.Held;
            if (held == null || !held.IsActor) { if (View.I != null) View.I.UseTicksLeft = 0; return; }
            var pos = Camera.main.transform.position;
            if (left < 25 && left % 4 == 0)
            {
                McSound.Play("random/eat", pos, 0.5f + 0.5f * Random.Range(0, 2), (Random.value - Random.value) * 0.2f + 1f);
                Particles.Burst(pos + Camera.main.transform.forward * 0.4f + Vector3.down * 0.15f, null, Hud.ItemColor(held), 5, 0.06f, 1.5f);
            }
            if (left == 0)
            {
                // A Slime Rancher fruit or veggie fills Minecraft's food bar: here that is Beatrix's energy, plus a little health.
                var ps = SRSingleton<SceneContext>.Instance.PlayerState;
                ps.SetEnergy(Mathf.Min(ps.GetMaxEnergy(), ps.GetCurrEnergy() + ps.GetMaxEnergy() / 4));
                ps.Heal(5);
                Consume();
                McSound.Play("random/burp", pos, 0.5f, Random.Range(0.9f, 1f));
            }
        }

        private static void PickUp(Identifiable actor)
        {
            var id = actor.id;
            var vac = actor.GetComponent<Vacuumable>();
            if (vac == null || !vac.enabled || vac.isCaptive() || Identifiable.IsGordo(id)) return;
            var stack = new ItemStack(id, 1);
            if (!Inventory.CanFit(stack)) return;
            ActorVisuals.Capture(actor.gameObject, id);
            var pos = actor.transform.position;
            Destroyer.DestroyActor(actor.gameObject, "SlimeCraft.PickUp");
            Inventory.Add(stack);
            TutorialPatches.Vac(id);
            View.I?.Swing();
            Hud.ShowItemName();
            McSound.Play("random/pop", pos, 0.2f, ((Random.value - Random.value) * 0.7f + 1f) * 2f);
            if (Identifiable.IsSlime(id)) McSound.Play("mob/slime/small", pos, 0.6f, Random.Range(0.9f, 1.3f));
        }

        private static GameObject Spawn(Identifiable.Id id, Vector3 pos, Quaternion rot)
        {
            var prefab = SRSingleton<GameContext>.Instance.LookupDirector.GetPrefab(id);
            if (prefab == null) return null;
            var region = SRSingleton<SceneContext>.Instance.RegionRegistry.GetCurrentRegionSetId();
            return SRBehaviour.InstantiateActor(prefab, region, pos, rot);
        }

        private static void PlaceActor(ItemStack held, Vector3 point, Vector3 normal)
        {
            var prefab = SRSingleton<GameContext>.Instance.LookupDirector.GetPrefab(held.Actor);
            if (prefab == null) return;
            float r = PhysicsUtil.RadiusOfObject(prefab);
            var cam = Camera.main.transform;
            var face = Quaternion.LookRotation(Vector3.ProjectOnPlane(-cam.forward, Vector3.up).normalized + Vector3.forward * 0.001f, Vector3.up);
            var go = Spawn(held.Actor, point + normal * (r + 0.05f), face);
            if (go == null) return;
            go.transform.localScale = prefab.transform.localScale;
            PhysicsUtil.RestoreFreezeRotationConstraints(go);
            Consume();
            TutorialPatches.Shoot(held.Actor);
            View.I?.Swing();
            McSound.Play(Identifiable.IsSlime(held.Actor) ? "mob/slime/small" : "dig/grass", go.transform.position, 0.8f, Random.Range(0.9f, 1.1f));
        }

        private static void Feed(GameObject slime, ItemStack food)
        {
            // Drop the food right in front of the slime's mouth; Slime Rancher's own appetite does the rest.
            var mouth = slime.transform.position + slime.transform.forward * 0.55f + Vector3.up * 0.25f;
            var cam = Camera.main.transform;
            var go = Spawn(food.Actor, mouth, Quaternion.identity);
            if (go == null) return;
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null) rb.velocity = (slime.transform.position - mouth).normalized * 1.5f;
            Consume();
            View.I?.Swing();
            McSound.Play("random/pop", mouth, 0.2f, 1.6f);
        }

        private static void PlaceBlock(ItemStack held, Target t)
        {
            var d = held.Block;
            var cam = Camera.main.transform;
            var n = Vector3Int.RoundToInt(t.Hit.normal);
            if (n == Vector3Int.zero) n = Vector3Int.up;

            // Two slabs of the same kind make a full block, as in Minecraft.
            if (d.Shape == BlockShape.Slab && t.Block != null && Blocks.World.TryGetValue(t.Block.Cell, out var slab) && slab.Def == d && slab.Rot != 2
                && ((slab.Rot == 0 && n.y > 0) || (slab.Rot == 1 && n.y < 0)))
            {
                Blocks.SetRot(slab, 2);
                McSound.Play(Blocks.PlaceSound(d), Blocks.Center(slab.Cell), 1f, 0.8f);
                Consume();
                return;
            }

            Vector3Int cell;
            bool replaceable = t.Block != null && Blocks.World.TryGetValue(t.Block.Cell, out var tb) && tb.Def.Shape == BlockShape.Cross && d.Shape != BlockShape.Cross;
            if (replaceable) { Blocks.Remove(t.Block.Cell, false); cell = t.Block.Cell; }
            else if (t.Block != null) cell = t.Block.Cell + n;
            else cell = Blocks.CellAt(t.Hit.point + t.Hit.normal * 0.5f);
            if (Blocks.World.ContainsKey(cell)) return;

            // Never inside the player or a slime, like Minecraft's entity collision check.
            var center = Blocks.Center(cell);
            if (!d.Walkable)
                foreach (var c in Physics.OverlapBox(center, Vector3.one * 0.499f, Quaternion.identity, (1 << 8) | (1 << 15), QueryTriggerInteraction.Ignore))
                    if (!c.isTrigger) return;

            int rot = 0;
            float Yaw(Vector3 v) => Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
            int Quarter(Vector3 v) => Mathf.RoundToInt(Yaw(v) / 90f) & 3;
            float hitY = t.Hit.point.y - cell.y; // where on the block face the click landed (0..1)
            switch (d.Shape)
            {
                case BlockShape.Torch:
                    if (n.y < 0) return; // no torches on ceilings
                    if (n.y > 0) rot = 0;
                    else if (n.z < 0) rot = 1;
                    else if (n.z > 0) rot = 2;
                    else if (n.x < 0) rot = 3;
                    else rot = 4;
                    break;
                case BlockShape.Column:
                    rot = n.y != 0 ? 0 : n.x != 0 ? 1 : 2;
                    break;
                case BlockShape.Slab:
                    rot = n.y < 0 ? 1 : n.y > 0 ? 0 : (hitY > 0.5f ? 1 : 0);
                    break;
                case BlockShape.Stairs:
                    rot = Quarter(cam.forward) + ((n.y < 0 || (n.y == 0 && hitY > 0.5f)) ? 4 : 0);
                    break;
                case BlockShape.Barrel:
                {
                    // The open top faces the player, up and down included.
                    var f = -cam.forward;
                    if (Mathf.Abs(f.y) > 0.7f) rot = f.y > 0 ? 0 : 1;
                    else if (Mathf.Abs(f.z) >= Mathf.Abs(f.x)) rot = f.z < 0 ? 2 : 3;
                    else rot = f.x < 0 ? 4 : 5;
                    break;
                }
                case BlockShape.Chest:
                    rot = Quarter(-cam.forward);
                    break;
                default:
                    if (d.HasFront) rot = Quarter(-cam.forward); // the front faces the player, like furnaces and pumpkins
                    break;
            }
            var placed = Blocks.Place(d, cell, rot, true, held.Contents != null ? (ItemStack[])held.Contents.Clone() : null);
            if (placed != null)
            {
                Consume();
                View.I?.Swing();
            }
        }

        /// <summary>Uses one of the held item, except in creative where nothing runs out.</summary>
        private static void Consume()
        {
            if (!GameMode.Creative) Inventory.TakeFromHeld();
        }

        private static void PickBlock()
        {
            var t = Current;
            ItemStack want = null;
            if (t.Block != null && Blocks.World.TryGetValue(t.Block.Cell, out var p)) want = new ItemStack(p.Def, 64);
            if (want == null) return;
            for (int i = 0; i < 9; i++)
                if (Inventory.Slots[i] != null && Inventory.Slots[i].SameItem(want)) { Select(i); return; }
            if (!GameMode.Creative) return;
            int slot = Inventory.Slots[Inventory.Selected] == null ? Inventory.Selected : -1;
            for (int i = 0; i < 9 && slot < 0; i++) if (Inventory.Slots[i] == null) slot = i;
            if (slot < 0) slot = Inventory.Selected;
            Inventory.Slots[slot] = want;
            Select(slot);
            Hud.ShowItemName();
            Persist.Dirty = true;
        }

        // ------------------------------------------------------------------------------------------------ drop

        public static void Drop(bool whole)
        {
            var held = Inventory.Held;
            if (held == null) return;
            int n = whole ? held.Count : 1;
            var one = held.Copy(n);
            Inventory.TakeFromHeld(n);
            Throw(one);
            View.I?.Swing();
        }

        /// <summary>Minecraft's item toss: 0.3 blocks/tick along the look, plus a little lift.</summary>
        public static void Throw(ItemStack s)
        {
            var cam = Camera.main.transform;
            var c = SRSingleton<SceneContext>.Instance.Player.GetComponent<vp_FPController>();
            var eye = c.SmoothPosition + Vector3.up * (Motor.EyeHeight - 0.3f);
            var vel = cam.forward * 6f + Vector3.up * 2f + new Vector3(Random.Range(-0.3f, 0.3f), 0, Random.Range(-0.3f, 0.3f));
            if (s.IsBlock)
            {
                ItemEntity.Spawn(s, eye + cam.forward * 0.3f, vel, 2f);
                return;
            }
            for (int i = 0; i < s.Count; i++)
            {
                var go = Spawn(s.Actor, eye + cam.forward * (0.6f + i * 0.05f), Quaternion.LookRotation(-cam.forward));
                if (go == null) continue;
                PhysicsUtil.RestoreFreezeRotationConstraints(go);
                var rb = go.GetComponent<Rigidbody>();
                if (rb != null) rb.velocity = vel + c.Velocity;
                // Counts as shot by Beatrix, so silos, feeders and the Plort Market accept it.
                var vac = go.GetComponent<Vacuumable>();
                if (vac != null) vac.Launch(Vacuumable.LaunchSource.PLAYER);
                TutorialPatches.Shoot(s.Actor);
            }
            McSound.Play("random/pop", eye, 0.2f, 0.8f);
        }

        // ------------------------------------------------------------------------------------------------ outline & cracks

        private static void ShowOutline(bool on)
        {
            if (outline != null) outline.SetActive(on);
            if (crack != null) crack.SetActive(on && breakProgress > 0);
        }

        private static void DrawOutline()
        {
            if (outline == null)
            {
                outline = new GameObject("SlimeCraft Outline");
                var mf = outline.AddComponent<MeshFilter>();
                var m = new Mesh();
                var v = new List<Vector3>();
                for (int i = 0; i < 8; i++) v.Add(new Vector3((i & 1) - 0.5f, ((i >> 1) & 1) - 0.5f, ((i >> 2) & 1) - 0.5f) * 1.004f);
                m.SetVertices(v);
                m.SetIndices(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7 }, MeshTopology.Lines, 0);
                mf.sharedMesh = m;
                var mat = new Material(Shader.Find("Hidden/Internal-Colored"));
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.SetInt("_Cull", 0);
                mat.color = new Color(0, 0, 0, 0.4f);
                outline.AddComponent<MeshRenderer>().sharedMaterial = mat;

                crack = new GameObject("SlimeCraft Crack");
                crack.AddComponent<MeshFilter>().sharedMesh = Blocks.CubeMesh();
                crackRenderer = crack.AddComponent<MeshRenderer>();
                crackRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                crackMats = new Material[10];
                for (int i = 0; i < 10; i++)
                {
                    crackMats[i] = Blocks.MakeMat(McAssets.Tex("blocks/destroy_stage_" + i + ".png"), true);
                    crackMats[i].renderQueue = 3100;
                }
            }
            var b = Current.Block;
            bool show = b != null && Blocks.World.TryGetValue(b.Cell, out var p) && p.Go != null;
            outline.SetActive(show);
            Bounds box = default;
            if (show)
            {
                var pl = Blocks.World[b.Cell];
                var cols = pl.Go.GetComponents<BoxCollider>();
                box = new Bounds(pl.Go.transform.TransformPoint(cols[0].center), Vector3.zero);
                foreach (var c in cols) box.Encapsulate(c.bounds);
                outline.transform.position = box.center;
                outline.transform.rotation = Quaternion.identity;
                outline.transform.localScale = box.size;
            }
            bool cracking = show && breaking == b.Cell && breakProgress > 0;
            crack.SetActive(cracking);
            if (cracking)
            {
                crack.transform.position = box.center;
                crack.transform.localScale = box.size * 1.003f;
                int stage = Mathf.Clamp((int)(Mathf.Lerp(prevBreakProgress, breakProgress, View.I != null ? View.I.TickDelta : 1f) * 10f), 0, 9);
                var m = crackMats[stage];
                crackRenderer.sharedMaterials = new[] { m, m, m, m };
            }
        }
    }

    /// <summary>Minecraft's block-break and eating particles: little textured bits that pop out and fall.</summary>
    public class Particles : MonoBehaviour
    {
        private Vector3 vel;
        private float life;

        public static void Burst(Vector3 at, BlockDef d, Color tint, int n, float size = 0.1f, float speed = 3f)
        {
            for (int i = 0; i < n; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Object.Destroy(go.GetComponent<Collider>());
                go.transform.position = at + Random.insideUnitSphere * 0.35f;
                go.transform.localScale = Vector3.one * size * Random.Range(0.6f, 1.2f);
                var r = go.GetComponent<MeshRenderer>();
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                if (d != null)
                {
                    r.sharedMaterial = Blocks.Mat(d.Side, BlockRender.Cutout);
                    // a random 4x4 pixel patch of the texture, like Minecraft's block-crack particle
                    var mf = go.GetComponent<MeshFilter>();
                    var mesh = Object.Instantiate(mf.sharedMesh);
                    float u = Random.Range(0, 12) / 16f, v = Random.Range(0, 12) / 16f;
                    mesh.uv = new[] { new Vector2(u, v), new Vector2(u + 0.25f, v), new Vector2(u, v + 0.25f), new Vector2(u + 0.25f, v + 0.25f) };
                    mf.sharedMesh = mesh;
                }
                else
                {
                    r.material = new Material(Shader.Find("Sprites/Default")) { color = tint * 0.8f };
                }
                var p = go.AddComponent<Particles>();
                p.vel = (go.transform.position - at).normalized * speed * Random.Range(0.3f, 1f) + Vector3.up * speed * 0.6f;
                p.life = Random.Range(0.4f, 1.0f);
            }
        }

        private void Update()
        {
            life -= Time.deltaTime;
            if (life <= 0) { Destroy(gameObject); return; }
            vel += Vector3.down * 16f * Time.deltaTime;
            vel *= Mathf.Pow(0.98f, Time.deltaTime * 20f);
            var next = transform.position + vel * Time.deltaTime;
            if (Physics.Linecast(transform.position, next, out var hit, -675375893, QueryTriggerInteraction.Ignore))
            {
                next = hit.point + hit.normal * 0.02f;
                vel = Vector3.zero;
            }
            transform.position = next;
            var cam = Camera.main;
            if (cam != null) transform.rotation = cam.transform.rotation;
        }
    }
}
