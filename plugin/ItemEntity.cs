using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// A dropped Minecraft item: a small spinning, bobbing block that falls with Minecraft's item physics and
    /// flies into the inventory when you walk over it.
    /// </summary>
    public class ItemEntity : MonoBehaviour
    {
        public ItemStack Stack;
        private Vector3 vel;
        private float age, pickupDelay, bobOffset;
        private Transform visual;
        private bool grounded;
        public static readonly List<ItemEntity> All = new List<ItemEntity>();

        public static ItemEntity Spawn(ItemStack s, Vector3 at, Vector3 velocity, float pickupDelay = 0.5f)
        {
            var go = new GameObject("mc item " + s.Name);
            go.transform.position = at;
            var e = go.AddComponent<ItemEntity>();
            e.Stack = s;
            e.vel = velocity;
            e.pickupDelay = pickupDelay;
            e.bobOffset = Random.value * Mathf.PI * 2f;
            var v = View.BuildVisual(s, go.transform, 0, out bool sprite);
            v.transform.localScale = Vector3.one * (sprite ? 0.5f : 0.25f);
            foreach (var r in v.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            e.visual = v.transform;
            All.Add(e);
            return e;
        }

        public static void ClearAll()
        {
            foreach (var e in All.ToArray()) if (e != null) Destroy(e.gameObject);
            All.Clear();
        }

        private void OnDestroy() => All.Remove(this);

        private void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0) return;
            age += dt;
            if (age > 300f) { Destroy(gameObject); return; } // Minecraft's 5 minute despawn

            // Minecraft item physics: gravity 0.04 b/t^2, drag 0.98, ground friction 0.6*0.98.
            if (!grounded) vel.y -= 0.04f * 400f * dt;
            float drag = Mathf.Pow(grounded ? 0.6f * 0.98f : 0.98f, dt * 20f);
            vel.x *= drag; vel.z *= drag; vel.y *= Mathf.Pow(0.98f, dt * 20f);
            var next = transform.position + vel * dt;
            if (Physics.SphereCast(transform.position + Vector3.up * 0.13f, 0.12f, (next - transform.position).normalized, out var hit, (next - transform.position).magnitude + 0.01f, -675375893, QueryTriggerInteraction.Ignore))
            {
                next = hit.point + hit.normal * 0.125f;
                if (hit.normal.y > 0.5f) { grounded = true; vel.y = 0; }
                else vel = Vector3.ProjectOnPlane(vel, hit.normal);
            }
            else if (grounded && !Physics.Raycast(transform.position + Vector3.up * 0.05f, Vector3.down, 0.2f, -675375893, QueryTriggerInteraction.Ignore))
                grounded = false;
            transform.position = next;

            // Spin 4.5 degrees per tick and bob, exactly like ItemEntityRenderer.
            visual.localRotation = Quaternion.Euler(0, (age * 20f * 4.5f) % 360f, 0);
            visual.localPosition = new Vector3(0, Mathf.Sin(age * 2f + bobOffset) * 0.1f + 0.1f + 0.125f, 0);

            // Merge with identical items lying close by.
            if (Time.frameCount % 20 == 0)
                foreach (var o in All)
                    if (o != this && o != null && o.Stack.SameItem(Stack) && (o.transform.position - transform.position).sqrMagnitude < 0.5f * 0.5f && o.Stack.Count + Stack.Count <= Stack.MaxStack)
                    {
                        Stack.Count += o.Stack.Count;
                        o.Stack.Count = 0;
                        Destroy(o.gameObject);
                    }

            if (age < pickupDelay || Screens.Open) return;
            var player = SRSingleton<SceneContext>.Instance?.Player;
            if (player == null) return;
            var feet = player.transform.position;
            var center = feet + Vector3.up * 0.9f;
            var d = center - transform.position;
            // Minecraft's player pickup box: the player's box grown by 1 block sideways and 0.5 up/down.
            if (Mathf.Abs(d.x) < 1.3f && Mathf.Abs(d.z) < 1.3f && transform.position.y > feet.y - 0.5f && transform.position.y < feet.y + 2.3f)
            {
                int took = Inventory.Add(Stack);
                if (took > 0)
                {
                    McSound.Play("random/pop", transform.position, 0.2f, ((Random.value - Random.value) * 0.7f + 1f) * 2f);
                    Stack.Count -= took;
                    if (Stack.Count <= 0) StartCoroutine(FlyIn(player.transform));
                }
            }
        }

        private System.Collections.IEnumerator FlyIn(Transform target)
        {
            enabled = false;
            var start = transform.position;
            for (float t = 0; t < 1f; t += Time.deltaTime / 0.15f)
            {
                if (target == null) break;
                transform.position = Vector3.Lerp(start, target.position + Vector3.up * 0.9f, t);
                yield return null;
            }
            Destroy(gameObject);
        }
    }

    /// <summary>
    /// SlimeCraft's own save, one file per Slime Rancher game, written at the moment Slime Rancher saves so the two
    /// never disagree about where a slime is (in the world or in Steve's inventory).
    /// </summary>
    public static class Persist
    {
        public static bool Dirty;
        public static string GameName;

        private static string Dir => Path.Combine(Plugin.Dir, "saves");

        private static string FileFor(string game)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) game = game.Replace(c, '_');
            return Path.Combine(Dir, game + ".txt");
        }

        public static void Save()
        {
            if (string.IsNullOrEmpty(GameName)) return;
            try
            {
                Directory.CreateDirectory(Dir);
                var text = "[inventory]\nmode " + (GameMode.Creative ? "creative" : "survival") + "\n" + Inventory.Serialize() + "[blocks]\n" + Blocks.Serialize();
                var f = FileFor(GameName);
                File.WriteAllText(f + ".tmp", text);
                if (File.Exists(f)) File.Delete(f);
                File.Move(f + ".tmp", f);
                Dirty = false;
                Plugin.Log.LogInfo("saved SlimeCraft data for " + GameName);
            }
            catch (System.Exception e) { Plugin.Log.LogError("SlimeCraft save failed: " + e); }
        }

        public static void Load(string game)
        {
            GameName = game;
            Inventory.Clear();
            Blocks.Clear();
            GameMode.Creative = false;
            Motor.Flying = false;
            var f = FileFor(game);
            if (!File.Exists(f))
            {
                StarterKit();
                Dirty = true;
                return;
            }
            var inv = new List<string>();
            var blocks = new List<string>();
            List<string> cur = null;
            foreach (var l in File.ReadAllLines(f))
            {
                if (l == "[inventory]") cur = inv;
                else if (l == "[blocks]") cur = blocks;
                else if (cur != null && l.Length > 0) cur.Add(l);
            }
            GameMode.Creative = inv.Contains("mode creative");
            Inventory.Deserialize(inv);
            Blocks.Deserialize(blocks);
            Dirty = false;
            Plugin.Log.LogInfo("loaded SlimeCraft data for " + game + ": " + Blocks.World.Count + " blocks");
        }

        /// <summary>First visit of a game: a few building blocks in the hotbar so there is something to try straight away.</summary>
        private static void StarterKit()
        {
            string[] kit = { "oak_planks", "oak_fence", "cobblestone", "glass", "torch", "white_wool" };
            foreach (var id in kit)
                if (Blocks.ById.TryGetValue(id, out var d)) Inventory.Add(new ItemStack(d, 64));
        }
    }
}
