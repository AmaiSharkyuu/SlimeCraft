using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Look-only copies of Subnautica's own 3D models (the knife, the scanner, titanium, a peeper...), so that what
    /// Amai holds, what lies on the ground and what Steve carries in third person is the real thing, not an icon.
    /// The prefab is cloned under an inactive holder (so none of its scripts wake up), stripped down to its meshes,
    /// and scaled to fit a Minecraft block so it sits in the hand like any item.
    /// </summary>
    public static class SnModels
    {
        private static GameObject stash;
        private static readonly Dictionary<TechType, GameObject> templates = new Dictionary<TechType, GameObject>();
        private static readonly HashSet<TechType> loading = new HashSet<TechType>();
        private static readonly HashSet<TechType> failed = new HashSet<TechType>();

        private static readonly HashSet<string> keep = new HashSet<string>
        {
            "Transform", "MeshFilter", "MeshRenderer", "SkinnedMeshRenderer", "LODGroup",
        };

        private static GameObject Stash()
        {
            if (stash == null)
            {
                stash = new GameObject("SubnautiCraft model stash");
                stash.SetActive(false);
                Object.DontDestroyOnLoad(stash);
            }
            return stash;
        }

        /// <summary>True once the model is ready; starts loading it the first time it is asked for.</summary>
        public static bool Ready(TechType t)
        {
            if (templates.ContainsKey(t)) return templates[t] != null;
            if (failed.Contains(t)) return false;
            if (loading.Add(t)) UWE.CoroutineHost.StartCoroutine(Load(t));
            return false;
        }

        public static bool Failed(TechType t) => failed.Contains(t);

        private static System.Collections.IEnumerator Load(TechType t)
        {
            var task = CraftData.GetPrefabForTechTypeAsync(t, false);
            yield return task;
            var prefab = task.GetResult();
            loading.Remove(t);
            if (prefab == null) { failed.Add(t); yield break; }
            GameObject clone = null;
            try
            {
                clone = Object.Instantiate(prefab, Stash().transform);
                clone.name = "model " + t;
                clone.transform.localPosition = Vector3.zero;
                clone.transform.localRotation = Quaternion.identity;
                Strip(clone);
                // the visual root: centred on its renderers' bounds, largest side = 1 (a Minecraft block)
                var root = new GameObject("sn " + t);
                root.transform.SetParent(Stash().transform, false);
                clone.transform.SetParent(root.transform, false);
                var b = LocalBounds(clone, root.transform);
                float size = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                if (size < 1e-4f || clone.GetComponentInChildren<Renderer>(true) == null) { Object.Destroy(root); failed.Add(t); yield break; }
                float k = 1f / size;
                clone.transform.localScale = clone.transform.localScale * k;
                clone.transform.localPosition = -b.center * k;
                templates[t] = root;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("model " + t + ": " + e.Message);
                if (clone != null) Object.Destroy(clone);
                failed.Add(t);
            }
        }

        private static void Strip(GameObject go)
        {
            // gameplay-only children
            foreach (var tr in go.GetComponentsInChildren<Transform>(true))
            {
                if (tr == null || tr == go.transform) continue;
                var n = tr.name.ToLowerInvariant();
                if (n.Contains("collider") || n.Contains("trigger") || n.Contains("light") && tr.GetComponent<Renderer>() == null) Object.DestroyImmediate(tr.gameObject);
            }
            // components depend on each other ([RequireComponent]): sweep until nothing more goes
            for (int pass = 0; pass < 8; pass++)
            {
                bool removed = false;
                foreach (var c in go.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || keep.Contains(c.GetType().Name)) continue;
                    try { Object.DestroyImmediate(c); removed = true; } catch { }
                }
                if (!removed) break;
            }
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                r.enabled = true;
                r.forceRenderingOff = false;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;
            }
            foreach (var lod in go.GetComponentsInChildren<LODGroup>(true)) lod.ForceLOD(0);
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
            return b;
        }

        /// <summary>A fresh copy of the model under `parent` (active), or null while it is not loaded.</summary>
        public static GameObject Make(TechType t, Transform parent, int layer)
        {
            if (!Ready(t)) return null;
            var go = Object.Instantiate(templates[t], Stash().transform);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            foreach (var tr in go.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer = layer;
            Sky.Apply(go);
            return go;
        }

        public static void ForgetAll()
        {
            foreach (var t in templates.Values) if (t != null) Object.Destroy(t);
            templates.Clear();
            failed.Clear();
        }
    }
}
