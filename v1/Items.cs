using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// One inventory stack. It is either a Minecraft block or a Slime Rancher actor (slime, plort, fruit,
    /// veggie, chicken...), so both games' things live side by side in the same hotbar.
    /// </summary>
    public class ItemStack
    {
        public BlockDef Block;
        public Identifiable.Id Actor = Identifiable.Id.NONE;
        public int Count;
        public ItemStack[] Contents; // a shulker box carries what was inside it

        public ItemStack(BlockDef b, int n) { Block = b; Count = n; }
        public ItemStack(Identifiable.Id a, int n) { Actor = a; Count = n; }

        public bool IsBlock => Block != null;
        public bool IsActor => Actor != Identifiable.Id.NONE;

        public ItemStack Copy(int n)
        {
            var c = IsBlock ? new ItemStack(Block, n) : new ItemStack(Actor, n);
            if (Contents != null) c.Contents = (ItemStack[])Contents.Clone();
            return c;
        }

        public bool SameItem(ItemStack o) => o != null && o.Block == Block && o.Actor == Actor && Contents == null && o.Contents == null;

        public bool HasContents
        {
            get
            {
                if (Contents == null) return false;
                foreach (var c in Contents) if (c != null) return true;
                return false;
            }
        }

        public int MaxStack
        {
            get
            {
                if (IsBlock) return Block.Shape == BlockShape.Shulker ? 1 : 64;
                if (Identifiable.IsLargo(Actor)) return 1;
                if (Identifiable.IsSlime(Actor) || Identifiable.IsAnimal(Actor)) return 16;
                if (Identifiable.IsToy(Actor) || Identifiable.IsCraft(Actor) || Identifiable.IsEcho(Actor)) return 16;
                return 64;
            }
        }

        public string Name
        {
            get
            {
                if (IsBlock) return Block.Name;
                try { return Identifiable.GetName(Actor, false) ?? Actor.ToString(); }
                catch { return Actor.ToString(); }
            }
        }

        public Sprite SrIcon
        {
            get
            {
                try { return SRSingleton<GameContext>.Instance.LookupDirector.GetIcon(Actor); }
                catch { return null; }
            }
        }

        /// <summary>"b:id:n" or "a:ID:n", plus "+item/item/..." for a shulker box's contents (they never nest).</summary>
        public string Save()
        {
            var s = IsBlock ? "b:" + Block.Id + ":" + Count : "a:" + Actor + ":" + Count;
            if (HasContents)
            {
                var parts = new string[Contents.Length];
                for (int i = 0; i < Contents.Length; i++) parts[i] = Contents[i] == null ? "-" : Contents[i].Save();
                s += "+" + string.Join("/", parts);
            }
            return s;
        }

        public static ItemStack Load(string s)
        {
            if (string.IsNullOrEmpty(s) || s == "-") return null;
            string inner = null;
            int plus = s.IndexOf('+');
            if (plus >= 0) { inner = s.Substring(plus + 1); s = s.Substring(0, plus); }
            var f = s.Split(':');
            if (f.Length < 3) return null;
            int n = int.Parse(f[2]);
            ItemStack r = null;
            if (f[0] == "b") r = Blocks.ById.TryGetValue(f[1], out var d) ? new ItemStack(d, n) : null;
            else
            {
                try { r = new ItemStack((Identifiable.Id)System.Enum.Parse(typeof(Identifiable.Id), f[1]), n); }
                catch { r = null; }
            }
            if (r != null && inner != null)
            {
                var items = inner.Split('/');
                r.Contents = new ItemStack[27];
                for (int i = 0; i < items.Length && i < 27; i++) r.Contents[i] = Load(items[i]);
            }
            return r;
        }

        /// <summary>A container's slots in one space-free token: items separated by commas.</summary>
        public static string SaveList(ItemStack[] list)
        {
            var parts = new string[list.Length];
            for (int i = 0; i < list.Length; i++) parts[i] = list[i] == null ? "-" : list[i].Save();
            return string.Join(",", parts);
        }

        public static ItemStack[] LoadList(string s, int size)
        {
            var r = new ItemStack[size];
            var parts = s.Split(',');
            for (int i = 0; i < parts.Length && i < size; i++) r[i] = Load(parts[i]);
            return r;
        }
    }

    /// <summary>Minecraft's player inventory: 9 hotbar slots (0..8) and 27 main slots (9..35).</summary>
    public static class Inventory
    {
        public static readonly ItemStack[] Slots = new ItemStack[36];
        public static int Selected;
        public static ItemStack Cursor; // what the mouse carries in an open inventory screen
        public static readonly ItemStack[] SlimeStorage = new ItemStack[45]; // the "Stockage de Slime" tab

        public static ItemStack Held => Slots[Selected];

        public static void Clear()
        {
            for (int i = 0; i < Slots.Length; i++) Slots[i] = null;
            for (int i = 0; i < SlimeStorage.Length; i++) SlimeStorage[i] = null;
            Cursor = null;
            Selected = 0;
        }

        /// <summary>Adds like Minecraft's insertStack: top up matching stacks (selected slot first), then the first empty slot.</summary>
        public static int Add(ItemStack s)
        {
            int left = s.Count;
            var order = new List<int> { Selected };
            for (int i = 0; i < 36; i++) if (i != Selected) order.Add(i);
            foreach (var i in order)
            {
                var t = Slots[i];
                if (t != null && t.SameItem(s) && t.Count < t.MaxStack)
                {
                    int m = Mathf.Min(left, t.MaxStack - t.Count);
                    t.Count += m; left -= m;
                    if (left == 0) break;
                }
            }
            for (int i = 0; i < 36 && left > 0; i++)
            {
                if (Slots[i] == null)
                {
                    int m = Mathf.Min(left, s.MaxStack);
                    Slots[i] = s.Copy(m); left -= m;
                }
            }
            if (left != s.Count) Persist.Dirty = true;
            return s.Count - left; // how many went in
        }

        public static bool CanFit(ItemStack s)
        {
            foreach (var t in Slots)
                if (t == null || (t.SameItem(s) && t.Count < t.MaxStack)) return true;
            return false;
        }

        public static void TakeFromHeld(int n = 1)
        {
            var h = Held;
            if (h == null) return;
            h.Count -= n;
            if (h.Count <= 0) Slots[Selected] = null;
            Persist.Dirty = true;
        }

        public static string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append("sel ").Append(Selected).Append('\n');
            for (int i = 0; i < 36; i++) sb.Append("slot ").Append(i).Append(' ').Append(Slots[i]?.Save() ?? "-").Append('\n');
            sb.Append("storage ").Append(ItemStack.SaveList(SlimeStorage)).Append('\n');
            return sb.ToString();
        }

        public static void Deserialize(IEnumerable<string> lines)
        {
            Clear();
            foreach (var l in lines)
            {
                var f = l.Split(' ');
                if (f[0] == "sel" && f.Length > 1) Selected = Mathf.Clamp(int.Parse(f[1]), 0, 8);
                else if (f[0] == "slot" && f.Length > 2) Slots[int.Parse(f[1])] = ItemStack.Load(f[2]);
                else if (f[0] == "storage" && f.Length > 1)
                {
                    var list = ItemStack.LoadList(f[1], SlimeStorage.Length);
                    for (int i = 0; i < SlimeStorage.Length; i++) SlimeStorage[i] = list[i];
                }
            }
        }
    }

    /// <summary>
    /// Look-only copies of Slime Rancher actors: the real meshes, materials, face and jiggle bones of a slime,
    /// with every gameplay component stripped, so the very same slime you picked up wobbles in Steve's hand.
    /// </summary>
    public static class ActorVisuals
    {
        private static GameObject stash;      // inactive holder: clones made under it never run Awake
        private static readonly Dictionary<Identifiable.Id, GameObject> templates = new Dictionary<Identifiable.Id, GameObject>();

        private static readonly HashSet<string> keep = new HashSet<string>
        {
            "Transform", "MeshFilter", "MeshRenderer", "SkinnedMeshRenderer", "Animator", "LODGroup",
            "SlimeAppearanceApplicator", "SlimeAppearanceObject", "RubberBoneEffect", "BodyMeshMarker",
        };

        private static GameObject Stash()
        {
            if (stash == null)
            {
                stash = new GameObject("SlimeCraft Visual Stash");
                stash.SetActive(false);
                Object.DontDestroyOnLoad(stash);
            }
            return stash;
        }

        public static void ForgetAll()
        {
            foreach (var t in templates.Values) if (t != null) Object.Destroy(t);
            templates.Clear();
        }

        private static readonly HashSet<Identifiable.Id> fromPrefab = new HashSet<Identifiable.Id>();

        /// <summary>Remember what a live actor looks like right now (its applied appearance, a specific largo...).</summary>
        public static void Capture(GameObject live, Identifiable.Id id)
        {
            if (live == null) return;
            if (templates.TryGetValue(id, out var old) && old != null && !fromPrefab.Contains(id)) return;
            if (old != null) Object.Destroy(old);
            var clone = Object.Instantiate(live, Stash().transform);
            clone.name = "visual " + id;
            Strip(clone, keepApplicator: false); // the appearance is already built into the copied children
            clone.transform.localPosition = Vector3.zero;
            clone.transform.localRotation = Quaternion.identity;
            clone.transform.localScale = live.transform.localScale;
            templates[id] = clone;
            fromPrefab.Remove(id);
        }

        /// <summary>A fresh visual instance for the given id, parented (and active) under `parent`.</summary>
        public static GameObject Make(Identifiable.Id id, Transform parent)
        {
            if (!templates.TryGetValue(id, out var t) || t == null)
            {
                // Nothing captured yet (e.g. right after loading a save): build from the prefab, letting the
                // slime appearance applicator assemble the look when the copy wakes up.
                GameObject prefab = null;
                try { prefab = SRSingleton<GameContext>.Instance.LookupDirector.GetPrefab(id); } catch { }
                if (prefab == null) return null;
                t = Object.Instantiate(prefab, Stash().transform);
                t.name = "visual " + id;
                t.transform.localScale = prefab.transform.localScale;
                Strip(t, keepApplicator: true);
                templates[id] = t;
                fromPrefab.Add(id);
            }
            var go = Object.Instantiate(t, Stash().transform); // still asleep under the inactive stash
            foreach (var app in go.GetComponentsInChildren<SlimeAppearanceApplicator>(true))
                app.AppearanceObjectProvider = new OwnedAppearanceProvider();
            go.transform.SetParent(parent, false);             // wakes up here
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = t.transform.localScale;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        /// <summary>Gives a visual copy its own appearance meshes instead of borrowing them from the game's pool.</summary>
        private class OwnedAppearanceProvider : SlimeAppearanceObjectProvider
        {
            public SlimeAppearanceObject Get(SlimeAppearanceObject prefab, GameObject targetParent)
            {
                return Object.Instantiate(prefab, targetParent.transform);
            }

            public void Put(SlimeAppearanceObject prefab, SlimeAppearanceObject obj)
            {
                if (obj != null) Object.Destroy(obj.gameObject);
            }
        }

        private static void Strip(GameObject go, bool keepApplicator)
        {
            // Drop child objects that only exist for gameplay (triggers, blob shadow).
            foreach (var tr in go.GetComponentsInChildren<Transform>(true))
            {
                if (tr == go.transform || tr == null) continue;
                if (tr.name == "Shadow" || tr.name.Contains("Trigger") || tr.name.Contains("TotemLinker")) Object.DestroyImmediate(tr.gameObject);
            }
            // Components can depend on each other ([RequireComponent]); keep sweeping until nothing more goes.
            for (int pass = 0; pass < 6; pass++)
            {
                bool removed = false;
                foreach (var c in go.GetComponentsInChildren<Component>(true))
                {
                    if (c == null) continue;
                    var n = c.GetType().Name;
                    if (keep.Contains(n) && (keepApplicator || n != "SlimeAppearanceApplicator")) continue;
                    try { Object.DestroyImmediate(c); removed = true; } catch { }
                }
                if (!removed) break;
            }
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.enabled = true;
            }
            foreach (var a in go.GetComponentsInChildren<Animator>(true)) a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }

        /// <summary>Visual bounds radius, used to scale a held slime to Minecraft's "block in hand" size.</summary>
        public static float Radius(GameObject go)
        {
            var b = new Bounds(go.transform.position, Vector3.zero);
            bool any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (r is ParticleSystemRenderer) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return any ? Mathf.Max(b.extents.x, b.extents.y, b.extents.z) : 0.5f;
        }

        public static void SetLayer(GameObject go, int layer)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        }
    }
}
