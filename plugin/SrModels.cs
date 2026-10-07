using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Look-only copies of Slime Rancher actors: the real meshes, materials, face and jiggle bones of a slime (or a
    /// plort, a carrot, a hen), with every gameplay component stripped, so the very slime you picked up wobbles in
    /// Steve's hand, lies on the ground as an item, or sits in an item frame. Copies are made under an inactive holder
    /// (so none of their scripts wake up) and scaled to fit a Minecraft block, like Minecraft's own item models.
    /// </summary>
    public static class SrModels
    {
        private static GameObject stash;
        private static readonly Dictionary<Identifiable.Id, GameObject> templates = new Dictionary<Identifiable.Id, GameObject>();
        private static readonly HashSet<Identifiable.Id> fromPrefab = new HashSet<Identifiable.Id>();
        private static readonly HashSet<Identifiable.Id> failed = new HashSet<Identifiable.Id>();

        private static readonly HashSet<string> keep = new HashSet<string>
        {
            "Transform", "MeshFilter", "MeshRenderer", "SkinnedMeshRenderer", "Animator", "LODGroup",
            "SlimeAppearanceApplicator", "SlimeAppearanceObject", "RubberBoneEffect", "BodyMeshMarker",
        };

        private static GameObject Stash()
        {
            if (stash == null)
            {
                stash = new GameObject("SlimeCraft model stash");
                stash.SetActive(false);
                Object.DontDestroyOnLoad(stash);
            }
            return stash;
        }

        public static bool Ready(Identifiable.Id t) => !failed.Contains(t);
        public static bool Failed(Identifiable.Id t) => failed.Contains(t);

        /// <summary>Remembers what a live actor looks like right now (its applied appearance, a particular largo...).</summary>
        public static void Capture(GameObject live, Identifiable.Id id)
        {
            if (live == null) return;
            if (templates.TryGetValue(id, out var old) && old != null && !fromPrefab.Contains(id)) return;
            if (old != null) Object.Destroy(old);
            var clone = Object.Instantiate(live, Stash().transform);
            clone.name = "model " + id;
            clone.transform.localPosition = Vector3.zero;
            clone.transform.localRotation = Quaternion.identity;
            clone.transform.localScale = live.transform.localScale;
            Strip(clone, keepApplicator: false); // the appearance is already built into the copied children
            templates[id] = Normalise(clone, id);
            fromPrefab.Remove(id);
        }

        private static GameObject Template(Identifiable.Id id)
        {
            if (templates.TryGetValue(id, out var t) && t != null) return t;
            GameObject prefab = null;
            try { prefab = SRSingleton<GameContext>.Instance.LookupDirector.GetPrefab(id); } catch { }
            if (prefab == null) { failed.Add(id); return null; }
            var clone = Object.Instantiate(prefab, Stash().transform);
            clone.name = "model " + id;
            clone.transform.localScale = prefab.transform.localScale;
            Strip(clone, keepApplicator: true); // a slime builds its look when the copy wakes up
            t = Normalise(clone, id);
            templates[id] = t;
            fromPrefab.Add(id);
            return t;
        }

        /// <summary>Wraps a clone in a root whose bounds are centred and whose largest side is 1 (a Minecraft block).</summary>
        private static GameObject Normalise(GameObject clone, Identifiable.Id id)
        {
            var root = new GameObject("sr " + id);
            root.transform.SetParent(Stash().transform, false);
            clone.transform.SetParent(root.transform, false);
            var b = LocalBounds(clone, root.transform);
            float size = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            if (size < 1e-4f) size = 1f;
            float k = 1f / size;
            clone.transform.localScale *= k;
            clone.transform.localPosition = -b.center * k;
            return root;
        }

        /// <summary>A fresh copy of the model under `parent` (active), or null if the game has no model for it.</summary>
        public static GameObject Make(Identifiable.Id id, Transform parent, int layer)
        {
            var t = Template(id);
            if (t == null) return null;
            var go = Object.Instantiate(t, Stash().transform); // still asleep under the inactive stash
            foreach (var app in go.GetComponentsInChildren<SlimeAppearanceApplicator>(true))
                app.AppearanceObjectProvider = new OwnedAppearanceProvider();
            go.transform.SetParent(parent, false);             // wakes up here
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            foreach (var tr in go.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer = layer;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        /// <summary>Gives a copy its own appearance meshes instead of borrowing them from the game's object pool.</summary>
        private class OwnedAppearanceProvider : SlimeAppearanceObjectProvider
        {
            public SlimeAppearanceObject Get(SlimeAppearanceObject prefab, GameObject targetParent) => Object.Instantiate(prefab, targetParent.transform);

            public void Put(SlimeAppearanceObject prefab, SlimeAppearanceObject obj)
            {
                if (obj != null) Object.Destroy(obj.gameObject);
            }
        }

        private static void Strip(GameObject go, bool keepApplicator)
        {
            // gameplay-only children: triggers, the blob shadow
            foreach (var tr in go.GetComponentsInChildren<Transform>(true))
            {
                if (tr == null || tr == go.transform) continue;
                if (tr.name == "Shadow" || tr.name.Contains("Trigger") || tr.name.Contains("TotemLinker")) Object.DestroyImmediate(tr.gameObject);
            }
            // components depend on each other ([RequireComponent]): sweep until nothing more goes
            for (int pass = 0; pass < 8; pass++)
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
                r.enabled = true;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;
            }
            foreach (var a in go.GetComponentsInChildren<Animator>(true)) a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }

        /// <summary>Bounds of every renderer, in `space`'s local coordinates.</summary>
        private static Bounds LocalBounds(GameObject go, Transform space)
        {
            bool any = false;
            var b = new Bounds();
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                Bounds lb;
                if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null) lb = smr.sharedMesh.bounds;
                else if (r.GetComponent<MeshFilter>() is MeshFilter mf && mf.sharedMesh != null) lb = mf.sharedMesh.bounds;
                else continue;
                var m = space.worldToLocalMatrix * r.transform.localToWorldMatrix;
                var c = lb.center; var e = lb.extents;
                for (int i = 0; i < 8; i++)
                {
                    var p = m.MultiplyPoint3x4(c + Vector3.Scale(e, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            return any ? b : new Bounds(Vector3.zero, Vector3.one);
        }

        public static void ForgetAll()
        {
            foreach (var t in templates.Values) if (t != null) Object.Destroy(t);
            templates.Clear();
            fromPrefab.Clear();
            failed.Clear();
        }
    }
}
