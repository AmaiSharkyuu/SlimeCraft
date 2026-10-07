using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Steve built from his real 64x64 skin with Minecraft's box-UV layout. Unity axes: +X is Steve's right,
    /// +Y up, +Z the way he faces; 1 skin pixel = 1/16 m before the player model's 0.9375 scale.
    /// </summary>
    public static class SteveMesh
    {
        private static Material skin, overlay;

        public static Material Skin
        {
            get
            {
                if (skin == null)
                {
                    skin = Blocks.MakeMat(McAssets.Tex("steve.png"), false);
                    skin.name = "mc:steve";
                    Object.DontDestroyOnLoad(skin);
                }
                return skin;
            }
        }

        public static Material Overlay
        {
            get
            {
                if (overlay == null)
                {
                    overlay = Blocks.MakeMat(McAssets.Tex("steve.png"), true);
                    overlay.name = "mc:steve overlay";
                    Object.DontDestroyOnLoad(overlay);
                }
                return overlay;
            }
        }

        /// <summary>
        /// A box of w x h x d skin pixels whose UVs start at (u, v) in the skin, placed from `min` (in pixels,
        /// relative to the part's pivot) and grown by `inflate` pixels on every side (for the hat/sleeve layer).
        /// </summary>
        public static Mesh Box(int u, int v, int w, int h, int d, Vector3 min, float inflate)
        {
            var mb = new Blocks.MeshBuilder(1);
            // Blocks.MeshBuilder works in 0..16 "block pixels" centred on the origin, so feed it pixel coordinates
            // shifted by +8 and undo that shift by hand afterwards.
            var a = min - Vector3.one * inflate + Vector3.one * 8;
            var b = min + new Vector3(w, h, d) + Vector3.one * inflate + Vector3.one * 8;
            // Texture rectangles, scaled to 16ths of the 64 px skin (MeshBuilder divides by 16).
            Vector4 R(float x0, float y0, float x1, float y1) => new Vector4(x0, y0, x1, y1) / 4f;
            Vector4 Uv(int face)
            {
                switch (face)
                {
                    case 0: return R(u + d + w, v, u + d, v + d);                 // top (mirrored: Steve's right is +X)
                    case 1: return R(u + d + w + w, v + d, u + d + w, v);         // bottom
                    case 3: return R(u + d, v + d, u + d + w, v + d + h);         // front (+Z)
                    case 2: return R(u + d + w + d, v + d, u + d + w + d + w, v + d + h); // back (-Z)
                    case 5: return R(u, v + d, u + d, v + d + h);                 // right side (+X)
                    default: return R(u + d + w, v + d, u + d + w + d, v + d + h); // left side (-X)
                }
            }
            mb.Box(a, b, Uv, f => 0);
            // MeshBuilder maps pixel p to p/16 - 0.5, so the +8 above lands min exactly on min/16 around the pivot,
            // and its UV helper reads rects in 16ths of the texture, which is why R() divides the 64 px skin by 4.
            var m = mb.Build();
            return m;
        }

        public static GameObject Part(string name, Transform parent, Vector3 pivotPx, int u, int v, int ou, int ov, int w, int h, int d, Vector3 minPx, float inflate, int layer)
        {
            var go = new GameObject(name);
            go.layer = layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pivotPx / 16f;
            var inner = new GameObject("skin") { layer = layer };
            inner.transform.SetParent(go.transform, false);
            inner.AddComponent<MeshFilter>().sharedMesh = Box(u, v, w, h, d, minPx, 0);
            inner.AddComponent<MeshRenderer>().sharedMaterial = Skin;
            var outer = new GameObject("overlay") { layer = layer };
            outer.transform.SetParent(go.transform, false);
            outer.AddComponent<MeshFilter>().sharedMesh = Box(ou, ov, w, h, d, minPx, inflate);
            outer.AddComponent<MeshRenderer>().sharedMaterial = Overlay;
            return go;
        }

        /// <summary>The right arm (wide model) with its pivot at the shoulder, as Minecraft draws it in first person.</summary>
        public static GameObject RightArm(Transform parent, int layer)
        {
            return Part("right arm", parent, Vector3.zero, 40, 16, 40, 32, 4, 12, 4, new Vector3(-1, -10, -2), 0.25f, layer);
        }
    }

    /// <summary>
    /// The whole Steve, standing where the player is: seen in third person (F5) and casting his shadow in first
    /// person, animated with Minecraft's biped formulas (limb swing, head look, sneak, arm swing).
    /// </summary>
    public class SteveBody : MonoBehaviour
    {
        public Transform Head, Body, RArm, LArm, RLeg, LLeg, HandAnchor;
        private Renderer[] renderers;
        private float bodyYaw;
        public float LimbAngle, LimbDistance, PrevLimbDistance;

        public static SteveBody Create()
        {
            var root = new GameObject("Steve");
            var s = root.AddComponent<SteveBody>();
            var model = new GameObject("model").transform;
            model.SetParent(root.transform, false);
            model.localScale = Vector3.one * 0.9375f;
            int L = 0;
            s.Body = SteveMesh.Part("body", model, new Vector3(0, 12, 0), 16, 16, 16, 32, 8, 12, 4, new Vector3(-4, 0, -2), 0.25f, L).transform;
            s.Head = SteveMesh.Part("head", s.Body, new Vector3(0, 12, 0), 0, 0, 32, 0, 8, 8, 8, new Vector3(-4, 0, -4), 0.5f, L).transform;
            s.RArm = SteveMesh.Part("right arm", s.Body, new Vector3(5, 10, 0), 40, 16, 40, 32, 4, 12, 4, new Vector3(-1, -10, -2), 0.25f, L).transform;
            s.LArm = SteveMesh.Part("left arm", s.Body, new Vector3(-5, 10, 0), 32, 48, 48, 48, 4, 12, 4, new Vector3(-3, -10, -2), 0.25f, L).transform;
            s.RLeg = SteveMesh.Part("right leg", model, new Vector3(1.9f, 12, 0), 0, 16, 0, 32, 4, 12, 4, new Vector3(-2, -12, -2), 0.25f, L).transform;
            s.LLeg = SteveMesh.Part("left leg", model, new Vector3(-1.9f, 12, 0), 16, 48, 0, 48, 4, 12, 4, new Vector3(-2, -12, -2), 0.25f, L).transform;
            var hand = new GameObject("hand").transform;
            hand.SetParent(s.RArm, false);
            hand.localPosition = new Vector3(1f, -10f, 0) / 16f;
            s.HandAnchor = hand;
            s.renderers = root.GetComponentsInChildren<Renderer>(true);
            return s;
        }

        public void SetVisible(bool full)
        {
            renderers = GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
                r.shadowCastingMode = full ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
        }

        /// <summary>Called every frame with interpolated state.</summary>
        public void Pose(Vector3 feet, float headYaw, float pitch, bool moving, bool sneaking, float swing, float tickDelta)
        {
            transform.position = feet;
            // Body yaw trails the head like Minecraft's: it turns with the movement, and never lags more than 50 degrees.
            float diff = Mathf.DeltaAngle(bodyYaw, headYaw);
            if (moving) bodyYaw = Mathf.MoveTowardsAngle(bodyYaw, headYaw, 720f * Time.deltaTime);
            else if (Mathf.Abs(diff) > 50f) bodyYaw = headYaw - Mathf.Sign(diff) * 50f;
            transform.rotation = Quaternion.Euler(0, bodyYaw, 0);

            float limb = Mathf.Lerp(PrevLimbDistance, LimbDistance, tickDelta);
            float a = LimbAngle;
            if (limb > 1f) limb = 1f;
            float rArm = Mathf.Cos(a * 0.6662f + Mathf.PI) * 2f * limb * 0.5f;
            float lArm = Mathf.Cos(a * 0.6662f) * 2f * limb * 0.5f;
            float rLeg = Mathf.Cos(a * 0.6662f) * 1.4f * limb;
            float lLeg = Mathf.Cos(a * 0.6662f + Mathf.PI) * 1.4f * limb;

            // Minecraft's attack swing, simplified to its main motion: the arm lifts forward and comes back down.
            float sw = Mathf.Sin(Mathf.Sqrt(swing) * Mathf.PI);
            rArm -= sw * 1.2f;

            float bodyPitch = sneaking ? 0.5f : 0f;
            Body.localRotation = Quaternion.Euler(bodyPitch * Mathf.Rad2Deg, 0, 0);
            Body.localPosition = new Vector3(0, sneaking ? 9.8f : 12f, sneaking ? -1.5f : 0) / 16f;
            if (sneaking) { rArm += 0.4f; lArm += 0.4f; }
            RArm.localRotation = Quaternion.Euler(-rArm * Mathf.Rad2Deg, 0, 0);
            LArm.localRotation = Quaternion.Euler(-lArm * Mathf.Rad2Deg, 0, 0);
            RLeg.localRotation = Quaternion.Euler(-rLeg * Mathf.Rad2Deg, 0, 0);
            LLeg.localRotation = Quaternion.Euler(-lLeg * Mathf.Rad2Deg, 0, 0);
            RLeg.localPosition = new Vector3(1.9f, sneaking ? 10f : 12f, sneaking ? 2.5f : 0) / 16f;
            LLeg.localPosition = new Vector3(-1.9f, sneaking ? 10f : 12f, sneaking ? 2.5f : 0) / 16f;
            float headRel = Mathf.Clamp(Mathf.DeltaAngle(bodyYaw, headYaw), -75f, 75f);
            Head.localRotation = Quaternion.Euler(0, headRel, 0) * Quaternion.Euler(pitch - bodyPitch * Mathf.Rad2Deg, 0, 0);
        }
    }
}
