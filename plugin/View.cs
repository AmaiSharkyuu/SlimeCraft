using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Everything Minecraft does with the camera and the first-person hand, ported from GameRenderer and
    /// HeldItemRenderer: view bobbing, hurt tilt, sprint FOV, F5 perspectives, the arm, the held item with its
    /// equip dip, swing and the lagging look rotation. Runs a 20 Hz "client tick" for state and interpolates
    /// every frame, like Minecraft.
    /// </summary>
    public class View : MonoBehaviour
    {
        public static View I;

        // ---- 20 Hz client state
        private float tickAcc;
        public float TickDelta;
        private float prevEquip, equip;
        private string shownKey = "";
        private ItemStack shown;
        private int swingTicks; private bool swinging; private float prevSwing, swing;
        private int lastAttackedTicks = 100;
        private float renderPitch, prevRenderPitch, renderYaw, prevRenderYaw;
        private float hSpeed, prevHSpeed, stride, prevStride;
        private int hurtTime; private float lastHealth = -1;
        private float fovMul = 1, prevFovMul = 1;
        public int UseTicksLeft;  // eating
        public int Perspective;   // 0 first person, 1 behind, 2 front

        // ---- objects
        private Transform cam;
        private Camera camera, weaponCam;
        private Transform fpRoot, arm, heldRoot;
        private GameObject heldVisual;
        private SteveBody steve;
        private GameObject steveHeld;
        private string steveHeldKey = "";
        private float baseFov = -1, lastSetFov = -1;

        private const int WeaponLayer = 31;

        public void Setup(GameObject player)
        {
            I = this;
            var srCam = player.GetComponentInChildren<SRFPCamera>();
            cam = srCam.transform;
            camera = cam.GetComponent<Camera>();
            var wc = cam.Find("WeaponCamera");
            if (wc != null) weaponCam = wc.GetComponent<Camera>();
            if (camera != null) camera.cullingMask &= ~(1 << 30);
            if (weaponCam != null) weaponCam.cullingMask &= ~(1 << 30);

            fpRoot = new GameObject("SlimeCraft FP").transform;
            fpRoot.gameObject.layer = WeaponLayer;
            fpRoot.SetParent(cam, false);
            arm = SteveMesh.RightArm(fpRoot, WeaponLayer).transform;
            heldRoot = new GameObject("held").transform;
            heldRoot.gameObject.layer = WeaponLayer;
            heldRoot.SetParent(fpRoot, false);

            steve = SteveBody.Create();
            steve.SetVisible(false);
            renderPitch = prevRenderPitch = Pitch;
            renderYaw = prevRenderYaw = Yaw;
        }

        private void OnDestroy()
        {
            if (fpRoot != null) Destroy(fpRoot.gameObject);
            if (steve != null) Destroy(steve.gameObject);
            if (I == this) I = null;
        }

        private float Pitch => cam != null ? Mathf.DeltaAngle(0, cam.eulerAngles.x) : 0;
        private float yawUnwrapped, lastYawRaw;
        private float Yaw => yawUnwrapped;

        public void Swing()
        {
            if (!swinging || swingTicks >= 3 || swingTicks < 0)
            {
                swingTicks = -1;
                swinging = true;
            }
        }

        public void ResetAttackCooldown() => lastAttackedTicks = 0;

        /// <summary>Attack strength 0..1, Minecraft's cooldown for an empty hand (attack speed 4: 5 ticks).</summary>
        public float Cooldown(float delta) => Mathf.Clamp01((lastAttackedTicks + delta) / 5f);

        private void Update()
        {
            if (cam == null) return;
            float raw = cam.eulerAngles.y;
            yawUnwrapped += Mathf.DeltaAngle(lastYawRaw, raw);
            lastYawRaw = raw;
            if (Time.timeScale == 0f) return;
            tickAcc += Time.deltaTime;
            int guard = 0;
            while (tickAcc >= 0.05f && guard++ < 10) { tickAcc -= 0.05f; ClientTick(); }
            TickDelta = tickAcc / 0.05f;
        }

        private void ClientTick()
        {
            lastAttackedTicks++;
            // swing
            prevSwing = swing;
            if (swinging)
            {
                swingTicks++;
                if (swingTicks >= 6) { swingTicks = 0; swinging = false; }
            }
            else swingTicks = 0;
            swing = swingTicks / 6f;

            // equip: lower to swap items, and dip after every attack (f^3 of the cooldown)
            prevEquip = equip;
            var held = Inventory.Held;
            var key = KeyOf(held);
            float f = Cooldown(1f);
            equip += Mathf.Clamp((key == shownKey ? f * f * f : 0f) - equip, -0.4f, 0.4f);
            if (equip < 0.1f && key != shownKey) { shownKey = key; shown = held; RebuildHeld(); }
            if (key == shownKey) shown = held;

            // look lag
            prevRenderPitch = renderPitch; prevRenderYaw = renderYaw;
            renderPitch += (Pitch - renderPitch) * 0.5f;
            renderYaw += (Yaw - renderYaw) * 0.5f;

            // bobbing
            prevHSpeed = hSpeed; prevStride = stride;
            hSpeed += Motor.HorizontalTickSpeed * 0.6f;
            float target = Motor.OnGround ? Mathf.Min(0.1f, Motor.HorizontalTickSpeed) : 0f;
            stride += (target - stride) * 0.4f;

            // limbs for the third-person body
            if (steve != null)
            {
                steve.PrevLimbDistance = steve.LimbDistance;
                float sp = Mathf.Min(Motor.HorizontalTickSpeed * 4f, 1f);
                steve.LimbDistance += (sp - steve.LimbDistance) * 0.4f;
                steve.LimbAngle += steve.LimbDistance;
            }

            // hurt tilt when Slime Rancher took health away
            var ps = SRSingleton<SceneContext>.Instance?.PlayerState;
            if (ps != null)
            {
                float h = ps.GetCurrHealth();
                if (lastHealth >= 0 && h < lastHealth - 0.5f) { hurtTime = 10; McSound.Play("damage/hit", cam.position, 1f, 1f); }
                lastHealth = h;
            }
            if (hurtTime > 0) hurtTime--;

            // sprint FOV
            prevFovMul = fovMul;
            float fm = Motor.Sprinting ? 1.15f : 1f;
            fovMul += (fm - fovMul) * 0.5f;

            if (UseTicksLeft > 0) Interaction.EatTick(--UseTicksLeft);
        }

        private static string KeyOf(ItemStack s) => s == null ? "" : s.IsBlock ? "b" + s.Block.Id : "a" + s.Actor;

        // ------------------------------------------------------------------------------------------------ held

        private void RebuildHeld()
        {
            if (heldVisual != null) Destroy(heldVisual);
            heldVisual = null;
            if (shown == null) return;
            heldVisual = BuildVisual(shown, heldRoot, WeaponLayer, out _);
        }

        /// <summary>Visual for any stack: a real Minecraft block mesh, or the real Slime Rancher actor normalised to a 1 m box.</summary>
        public static GameObject BuildVisual(ItemStack s, Transform parent, int layer, out bool isSprite)
        {
            isSprite = false;
            var holder = new GameObject("visual");
            holder.transform.SetParent(parent, false);
            if (s.IsBlock)
            {
                var d = s.Block;
                var mf = holder.AddComponent<MeshFilter>();
                var mr = holder.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                if (d.IsSprite)
                {
                    // Plants and torches are flat extruded sprites in the hand, like Minecraft's item/generated.
                    mf.sharedMesh = SpriteMesh.Extrude(Blocks.TexOf(d.Side));
                    mr.sharedMaterial = Blocks.Mat(d.Side, BlockRender.Cutout);
                    isSprite = true;
                }
                else
                {
                    switch (d.Shape)
                    {
                        case BlockShape.Fence: mf.sharedMesh = Blocks.FenceMesh(false, false, true, true); break;
                        case BlockShape.Slab: mf.sharedMesh = Blocks.SlabMesh(false); break;
                        case BlockShape.Stairs: mf.sharedMesh = Blocks.StairsMesh(false); break;
                        case BlockShape.Carpet: mf.sharedMesh = Blocks.CarpetMesh(); break;
                        case BlockShape.Chest: mf.sharedMesh = Blocks.ChestBase(); break;
                        case BlockShape.Shulker: mf.sharedMesh = Blocks.ShulkerBase(); break;
                        default: mf.sharedMesh = Blocks.CubeMesh(); break;
                    }
                    mr.sharedMaterials = Blocks.MatsFor(d);
                    if (d.Shape == BlockShape.Chest || d.Shape == BlockShape.Shulker)
                    {
                        var lid = new GameObject("lid");
                        lid.transform.SetParent(holder.transform, false);
                        if (d.Shape == BlockShape.Chest) lid.transform.localPosition = new Vector3(0, 9f / 16f - 0.5f, 1f / 16f - 0.5f);
                        lid.AddComponent<MeshFilter>().sharedMesh = d.Shape == BlockShape.Chest ? Blocks.ChestLid() : Blocks.ShulkerLid();
                        var lr = lid.AddComponent<MeshRenderer>();
                        lr.sharedMaterials = mr.sharedMaterials;
                        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    }
                }
            }
            else
            {
                var v = ActorVisuals.Make(s.Actor, holder.transform);
                if (v != null)
                {
                    float r = Mathf.Max(0.05f, ActorVisuals.Radius(v));
                    var b = new Bounds(v.transform.position, Vector3.zero);
                    bool any = false;
                    foreach (var rr in v.GetComponentsInChildren<Renderer>())
                    {
                        if (rr is ParticleSystemRenderer) continue;
                        if (!any) { b = rr.bounds; any = true; } else b.Encapsulate(rr.bounds);
                    }
                    float k = 0.5f / r;
                    v.transform.localScale *= k;
                    v.transform.localPosition = -(b.center - holder.transform.position) * k;
                }
            }
            ActorVisuals.SetLayer(holder, layer);
            return holder;
        }

        // ------------------------------------------------------------------------------------------------ frame

        /// <summary>Called after Slime Rancher's camera has placed itself for the frame.</summary>
        public void AfterCamera(vp_FPController controller)
        {
            if (cam == null || controller == null) return;
            if (Time.timeScale == 0f && !Screens.Open) return;
            float td = TickDelta;
            float pitch = Pitch;

            // Eye position: the interpolated physics body plus Minecraft's eye height.
            var smooth = controller.SmoothPosition;
            var eye = smooth + Vector3.up * Motor.EyeHeight;
            var rot = Quaternion.Euler(pitch, cam.eulerAngles.y, 0);

            // Minecraft's view transform, in its own (right-handed, looking down -z) view space.
            var B = Matrix4x4.identity;
            if (hurtTime > 0)
            {
                float g = (hurtTime - td) / 10f;
                if (g > 0)
                {
                    g = Mathf.Sin(g * g * g * g * Mathf.PI);
                    B = B * Matrix4x4.Rotate(Quaternion.AngleAxis(-g * 14f, Vector3.forward));
                }
            }
            float walk = hSpeed - prevHSpeed;
            float gg = -(hSpeed + walk * td);
            float h = Mathf.Lerp(prevStride, stride, td);
            B = B * Matrix4x4.Translate(new Vector3(Mathf.Sin(gg * Mathf.PI) * h * 0.5f, -Mathf.Abs(Mathf.Cos(gg * Mathf.PI) * h), 0));
            B = B * Matrix4x4.Rotate(Quaternion.AngleAxis(Mathf.Sin(gg * Mathf.PI) * h * 3f, Vector3.forward));
            B = B * Matrix4x4.Rotate(Quaternion.AngleAxis(Mathf.Abs(Mathf.Cos(gg * Mathf.PI - 0.2f) * h) * 5f, Vector3.right));

            var S = Matrix4x4.Scale(new Vector3(1, 1, -1));
            var camDelta = S * B.inverse * S;

            // Perspective (F5): pull back 4 blocks, stopping at walls, or turn around for the front view.
            var camRot = rot;
            var camPos = eye;
            if (Perspective != 0)
            {
                if (Perspective == 2) camRot = Quaternion.Euler(-pitch, cam.eulerAngles.y + 180f, 0);
                var back = camRot * Vector3.back;
                float dist = 4f;
                if (Physics.SphereCast(eye, 0.1f, back, out var hit, 4f, -675375893 & ~(1 << 8), QueryTriggerInteraction.Ignore))
                    dist = Mathf.Max(0.1f, hit.distance);
                camPos = eye + back * dist;
            }
            cam.position = camPos + camRot * (Vector3)camDelta.GetColumn(3);
            cam.rotation = camRot * camDelta.rotation;

            // Sprint FOV, on top of whatever FOV Slime Rancher's options set.
            if (camera != null)
            {
                if (baseFov < 0 || Mathf.Abs(camera.fieldOfView - lastSetFov) > 0.01f) baseFov = camera.fieldOfView;
                lastSetFov = baseFov * Mathf.Lerp(prevFovMul, fovMul, td);
                camera.fieldOfView = lastSetFov;
            }

            // Body: always there for its shadow; fully visible in third person.
            if (steve != null)
            {
                steve.SetVisible(Perspective != 0);
                steve.Pose(smooth, cam.eulerAngles.y - (Perspective == 2 ? 180f : 0f), pitch, Motor.Moving, Motor.Sneaking, Mathf.Lerp(prevSwing, swing, td), td);
                UpdateSteveHeld();
            }

            fpRoot.gameObject.SetActive(Perspective == 0 && !Screens.HideHand);
            if (Perspective == 0) PoseHand(B, td, pitch);
        }

        private void UpdateSteveHeld()
        {
            var key = shownKey;
            if (key == steveHeldKey && (steveHeld != null || shown == null)) return;
            steveHeldKey = key;
            if (steveHeld != null) Destroy(steveHeld);
            steveHeld = null;
            if (shown == null) return;
            steveHeld = BuildVisual(shown, steve.HandAnchor, 0, out bool sprite);
            steveHeld.transform.localScale = Vector3.one * (sprite ? 0.55f : 0.375f) / 0.9375f;
            steveHeld.transform.localPosition = new Vector3(0, -0.06f, 0.1f);
            steveHeld.transform.localRotation = Quaternion.Euler(sprite ? 0 : 75, 45, 0);
            foreach (var r in steveHeld.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        }

        // Rotations in Minecraft's matrix stack, about its own axes, in degrees.
        private static Matrix4x4 RX(float d) => Matrix4x4.Rotate(Quaternion.AngleAxis(d, Vector3.right));
        private static Matrix4x4 RY(float d) => Matrix4x4.Rotate(Quaternion.AngleAxis(d, Vector3.up));
        private static Matrix4x4 RZ(float d) => Matrix4x4.Rotate(Quaternion.AngleAxis(d, Vector3.forward));
        private static Matrix4x4 T(float x, float y, float z) => Matrix4x4.Translate(new Vector3(x, y, z));

        private static void Apply(Transform t, Matrix4x4 m)
        {
            t.localPosition = m.GetColumn(3);
            Vector3 c0 = m.GetColumn(0), c1 = m.GetColumn(1), c2 = m.GetColumn(2);
            t.localRotation = Quaternion.LookRotation(c2, c1);
            t.localScale = new Vector3(c0.magnitude, c1.magnitude, c2.magnitude);
        }

        private void PoseHand(Matrix4x4 bob, float td, float pitch)
        {
            var S = Matrix4x4.Scale(new Vector3(1, 1, -1));
            var A = Matrix4x4.Scale(new Vector3(-1, -1, -1));   // Steve's model space -> Minecraft model space
            var Bk = Matrix4x4.Scale(new Vector3(1, 1, -1));    // block/item model space

            var M = bob;
            float lagP = Mathf.Lerp(prevRenderPitch, renderPitch, td), lagY = Mathf.Lerp(prevRenderYaw, renderYaw, td);
            M = M * RX((pitch - lagP) * 0.1f) * RY((Yaw - lagY) * 0.1f);

            float sw = Mathf.Lerp(prevSwing, swing, td);
            float eq = 1f - Mathf.Lerp(prevEquip, equip, td);
            const float i = 1f; // right arm

            if (shown == null)
            {
                // renderArmHoldingItem
                arm.gameObject.SetActive(true);
                heldRoot.gameObject.SetActive(false);
                float g = Mathf.Sqrt(sw);
                float hx = -0.3f * Mathf.Sin(g * Mathf.PI), iy = 0.4f * Mathf.Sin(g * Mathf.PI * 2f), jz = -0.4f * Mathf.Sin(sw * Mathf.PI);
                M = M * T(i * (hx + 0.64000005f), iy - 0.6f + eq * -0.6f, jz - 0.71999997f);
                M = M * RY(i * 45f);
                float k = Mathf.Sin(sw * sw * Mathf.PI), l = Mathf.Sin(g * Mathf.PI);
                M = M * RY(i * l * 70f) * RZ(i * k * -20f);
                M = M * T(i * -1f, 3.6f, 3.5f) * RZ(i * 120f) * RX(200f) * RY(i * -135f) * T(i * 5.6f, 0, 0);
                // PlayerEntityModel.rightArm pivot (-5, 2, 0) in model pixels.
                M = M * T(-5f / 16f, 2f / 16f, 0);
                Apply(arm, S * M * A);
                return;
            }

            arm.gameObject.SetActive(false);
            heldRoot.gameObject.SetActive(true);
            if (UseTicksLeft > 0)
            {
                // applyEatOrDrinkTransformation
                float f = UseTicksLeft - td + 1f;
                float g = f / 32f;
                if (g < 0.8f) M = M * T(0, Mathf.Abs(Mathf.Cos(f / 4f * Mathf.PI) * 0.1f), 0);
                float hh = 1f - Mathf.Pow(g, 27f);
                M = M * T(hh * 0.6f * i, hh * -0.5f, 0) * RY(i * hh * 90f) * RX(hh * 10f) * RZ(i * hh * 30f);
                M = M * T(i * 0.56f, -0.52f + eq * -0.6f, -0.72f);
            }
            else
            {
                float o = -0.4f * Mathf.Sin(Mathf.Sqrt(sw) * Mathf.PI);
                float m = 0.2f * Mathf.Sin(Mathf.Sqrt(sw) * Mathf.PI * 2f);
                float f = -0.2f * Mathf.Sin(sw * Mathf.PI);
                M = M * T(i * o, m, f);
                M = M * T(i * 0.56f, -0.52f + eq * -0.6f, -0.72f);            // applyEquipOffset
                float s1 = Mathf.Sin(sw * sw * Mathf.PI);                      // applySwingOffset
                M = M * RY(i * (45f + s1 * -20f));
                float g = Mathf.Sin(Mathf.Sqrt(sw) * Mathf.PI);
                M = M * RZ(i * g * -20f) * RX(g * -80f) * RY(i * -45f);
            }

            // Display transform of the model (firstperson_righthand), then the model's own -0.5 centering
            // (our meshes are already centred, which the Bk conversion accounts for).
            if (shown.IsBlock && shown.Block.IsSprite)
                M = M * T(1.13f / 16f, 3.2f / 16f, 1.13f / 16f) * RX(0) * RY(-90f) * RZ(25f) * Matrix4x4.Scale(Vector3.one * 0.68f);
            else if (shown.IsBlock)
                M = M * RY(45f) * Matrix4x4.Scale(Vector3.one * 0.4f);
            else
                // A Slime Rancher actor sits like a block, turned so its face looks back at you.
                M = M * T(0, 0.05f, 0) * RY(200f) * Matrix4x4.Scale(Vector3.one * 0.5f);
            Apply(heldRoot, S * M * Bk);
        }
    }

    /// <summary>Minecraft's item/generated model: the sprite extruded one pixel thick, with side faces only where pixels are opaque.</summary>
    public static class SpriteMesh
    {
        private static readonly System.Collections.Generic.Dictionary<Texture2D, Mesh> cache = new System.Collections.Generic.Dictionary<Texture2D, Mesh>();

        public static Mesh Extrude(Texture2D tex)
        {
            if (cache.TryGetValue(tex, out var m) && m != null) return m;
            int w = tex.width, h = tex.height;
            var px = tex.GetPixels32();
            bool Op(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && px[y * w + x].a > 0;
            var mb = new Blocks.MeshBuilder(1);
            float z0 = -0.5f / 16f, z1 = 0.5f / 16f;
            Vector3 P(float x, float y, float z) => new Vector3(x / w - 0.5f, y / h - 0.5f, z);
            Vector2 U(float x, float y) => new Vector2(x / w, y / h);
            mb.Quad(0, P(0, 0, z0), P(0, h, z0), P(w, h, z0), P(w, 0, z0), Vector3.back, U(0, 0), U(0, 1), U(1, 1), U(1, 0));
            mb.Quad(0, P(w, 0, z1), P(w, h, z1), P(0, h, z1), P(0, 0, z1), Vector3.forward, U(1, 0), U(1, 1), U(0, 1), U(0, 0));
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (!Op(x, y)) continue;
                    var c = U(x + 0.5f, y + 0.5f);
                    if (!Op(x - 1, y)) mb.Quad(0, P(x, y, z1), P(x, y + 1, z1), P(x, y + 1, z0), P(x, y, z0), Vector3.left, c, c, c, c);
                    if (!Op(x + 1, y)) mb.Quad(0, P(x + 1, y, z0), P(x + 1, y + 1, z0), P(x + 1, y + 1, z1), P(x + 1, y, z1), Vector3.right, c, c, c, c);
                    if (!Op(x, y - 1)) mb.Quad(0, P(x, y, z0), P(x + 1, y, z0), P(x + 1, y, z1), P(x, y, z1), Vector3.down, c, c, c, c);
                    if (!Op(x, y + 1)) mb.Quad(0, P(x, y + 1, z1), P(x + 1, y + 1, z1), P(x + 1, y + 1, z0), P(x, y + 1, z0), Vector3.up, c, c, c, c);
                }
            m = mb.Build();
            m.name = "sprite " + tex.name;
            Object.DontDestroyOnLoad(m);
            cache[tex] = m;
            return m;
        }
    }
}
