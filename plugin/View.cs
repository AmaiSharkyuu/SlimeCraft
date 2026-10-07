using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Minecraft's camera and first-person hand, ported from GameRenderer and HeldItemRenderer: view bobbing,
    /// the hurt tilt, sprint / flight / bow FOV, the three F5 perspectives with wall clipping, the arm, the held
    /// item with its equip dip, swing, look lag, eating / bow / trident / crossbow poses and the model's own display
    /// transforms. A 20 Hz client tick holds the state and every frame interpolates, like Minecraft.
    ///
    /// The camera offsets are applied in the main camera's OnPreCull and taken back in OnPostRender, so game logic
    /// (aiming, raycasts, Slime Rancher's own prompts) always sees the plain, unbobbed eye. The hand is drawn by
    /// Slime Rancher's weapon camera (a child of the main camera), so it never clips into walls.
    /// </summary>
    public class View : MonoBehaviour
    {
        public static View I;

        // ---- 20 Hz state
        private float tickAcc;
        public float TickDelta;
        private float prevEquip, equip;
        private string shownKey = "";
        private ItemStack shown;
        private int swingTicks; private bool swinging; private float prevSwing, swing;
        public int LastAttackedTicks = 100;
        private float renderPitch, prevRenderPitch, renderYaw, prevRenderYaw;
        private float hSpeed, prevHSpeed, stride, prevStride;
        private float fovMul = 1, prevFovMul = 1;
        private float camY, prevCamY;
        public int Perspective;   // 0 first person, 1 behind, 2 front
        public bool HideGui;      // F1

        // ---- objects
        private Transform camT;
        private Camera cam;
        private Transform fpRoot, armT, heldRoot, heldPivot;
        private GameObject heldVisual;
        private MPart arm;
        public PlayerBody Body;
        private float baseFov = -1, lastSetFov = -1;
        private Vector3 savedPos; private Quaternion savedRot; private bool applied;
        public const int HandLayer = 31; // Slime Rancher's "Weapon" layer, drawn by its weapon camera
        private bool slim = true;

        public static void Create(GameObject player)
        {
            var camGo = player.GetComponentInChildren<SRFPCamera>().gameObject;
            var v = camGo.GetComponent<View>() ?? camGo.AddComponent<View>();
            v.Setup();
        }

        private void Setup()
        {
            I = this;
            cam = GetComponent<Camera>();
            camT = transform;
            if (fpRoot != null) Destroy(fpRoot.gameObject);
            fpRoot = new GameObject("SlimeCraft FP").transform;
            fpRoot.gameObject.layer = HandLayer;
            fpRoot.SetParent(camT, false);
            var skin = PlayerSkin.Texture;
            slim = PlayerSkin.Slim;
            arm = Models.RightArm(slim);
            var armMat = BlockWorld.MakeMat(1, skin);
            var armHolder = new GameObject("arm holder") { layer = HandLayer };
            armHolder.transform.SetParent(fpRoot, false);
            armT = armHolder.transform;
            arm.Build(armT, armMat, 64, 64, HandLayer);
            Sky.Apply(fpRoot.gameObject);
            heldPivot = new GameObject("held pivot") { layer = HandLayer }.transform;
            heldPivot.SetParent(fpRoot, false);
            heldRoot = heldPivot;
            if (Body != null) Destroy(Body.gameObject);
            Body = PlayerBody.Create(skin, slim);
            renderPitch = prevRenderPitch = Pitch;
            renderYaw = prevRenderYaw = Yaw;
            camY = prevCamY = Motor.EyeHeight;
        }

        private void OnDestroy()
        {
            if (fpRoot != null) Destroy(fpRoot.gameObject);
            if (Body != null) Destroy(Body.gameObject);
            if (I == this) I = null;
        }

        public float Pitch => camT != null ? Mathf.DeltaAngle(0, camT.eulerAngles.x) : 0;
        private float yawUnwrapped, lastYawRaw;
        public float Yaw => yawUnwrapped;

        public void Swing()
        {
            if (!swinging || swingTicks >= 3 || swingTicks < 0)
            {
                swingTicks = -1;
                swinging = true;
            }
        }

        public void ResetAttackCooldown() => LastAttackedTicks = 0;

        /// <summary>PlayerEntity.getAttackCooldownProgress: 0..1, period = 20 / attack speed ticks.</summary>
        public float Cooldown(float delta)
        {
            var h = Inventory.Held;
            float speed = h != null && h.IsMc ? h.Item.AttackSpeed : 4f;
            if (Effects.Has("haste")) speed *= 1f + 0.1f * (Effects.Amplifier("haste") + 1);
            if (Effects.Has("mining_fatigue")) speed *= 1f - 0.1f * (Effects.Amplifier("mining_fatigue") + 1);
            float period = 20f / Mathf.Max(0.1f, speed);
            return Mathf.Clamp01((LastAttackedTicks + delta) / period);
        }

        private void Update()
        {
            Restore();
            if (camT == null) return;
            float raw = camT.eulerAngles.y;
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
            LastAttackedTicks++;
            prevSwing = swing;
            if (swinging)
            {
                swingTicks++;
                int dur = SwingDuration();
                if (swingTicks >= dur) { swingTicks = 0; swinging = false; }
            }
            else swingTicks = 0;
            swing = swingTicks / (float)SwingDuration();

            // equip: lower the hand to swap items, and dip after every attack (cooldown cubed)
            prevEquip = equip;
            var held = Inventory.Held;
            var key = KeyOf(held);
            float f = Cooldown(1f);
            bool same = key == shownKey;
            equip += Mathf.Clamp((same ? f * f * f : 0f) - equip, -0.4f, 0.4f);
            if (equip < 0.1f && !same) { shownKey = key; shown = held; RebuildHeld(); }
            if (same) shown = held;
            if (same && held != null && shown != null && RebuildForUse()) RebuildHeld();

            prevRenderPitch = renderPitch; prevRenderYaw = renderYaw;
            renderPitch += (Pitch - renderPitch) * 0.5f;
            renderYaw += (Yaw - renderYaw) * 0.5f;

            prevHSpeed = hSpeed; prevStride = stride;
            hSpeed += Motor.HorizontalTickSpeed * 0.6f;
            float target = Motor.OnGround && !Motor.InWater ? Mathf.Min(0.1f, Motor.HorizontalTickSpeed) : 0f;
            stride += (target - stride) * 0.4f;

            if (Body != null) Body.Tick();

            // FOV: AbstractClientPlayerEntity.getFovMultiplier
            prevFovMul = fovMul;
            float fm = 1f;
            if (Motor.Flying) fm *= 1.1f;
            fm *= (Effects.SpeedMultiplier() * (Motor.Sprinting ? 1.3f : 1f) / 1f + 1f) / 2f;
            if (Interaction.Using && Interaction.UseAction == "bow")
            {
                float g = Interaction.UseElapsed / 20f;
                g = g > 1f ? 1f : g * g;
                fm *= 1f - g * 0.15f;
            }
            fovMul += (fm - fovMul) * 0.5f;

            prevCamY = camY;
            camY += (Motor.EyeHeight - camY) * 0.5f;
        }

        /// <summary>LivingEntity.getHandSwingDuration: 6 ticks, faster with Haste, slower with Mining Fatigue.</summary>
        private static int SwingDuration()
        {
            if (Effects.Has("haste")) return 6 - (1 + Effects.Amplifier("haste"));
            if (Effects.Has("mining_fatigue")) return 6 + (1 + Effects.Amplifier("mining_fatigue")) * 2;
            return 6;
        }

        private static string KeyOf(ItemStack s) => s == null ? "" : s.Id + "|" + s.Damage + "|" + (s.Enchanted ? "e" : "") + s.Potion + s.Color + (s.IsSr ? (SrModels.Ready(s.Actor) ? "3d" : SrModels.Failed(s.Actor) ? "icon" : "?") : "");

        private string lastUseModel;

        private bool RebuildForUse()
        {
            if (shown == null || !shown.IsMc || shown.Item.Overrides.Count == 0) return false;
            var m = ItemRender.ModelName(shown, Interaction.Predicates(shown));
            if (m == lastUseModel) return false;
            return true;
        }

        private void RebuildHeld()
        {
            if (heldVisual != null) Destroy(heldVisual);
            heldVisual = null;
            lastUseModel = null;
            if (shown == null) return;
            var pred = Interaction.Predicates(shown);
            if (shown.IsMc) lastUseModel = ItemRender.ModelName(shown, pred);
            heldVisual = ItemRender.Build(shown, heldRoot, HandLayer, "firstperson_righthand", pred);
            ActorLayer(heldVisual, HandLayer);
            Sky.Apply(fpRoot.gameObject);
        }

        private static void ActorLayer(GameObject go, int layer)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        }

        // ------------------------------------------------------------------------------------------------ frame

        private Vector3 InterpolatedEyeOffset()
        {
            // the player moves smoothly every frame; only the eye height eases over a few ticks (sneaking)
            float y = Mathf.Lerp(prevCamY, camY, TickDelta) - Motor.EyeHeight;
            return Vector3.up * y;
        }

        /// <summary>
        /// After Slime Rancher's camera placed itself: put it at Minecraft's plain eye (the feet plus the eased eye
        /// height), so everything that aims from the camera aims from where Minecraft would.
        /// </summary>
        public void PlaceEye()
        {
            if (!Game.Active || camT == null || Motor.SuspendedByGame) return;
            camT.position = SrWorld.Feet + Vector3.up * Mathf.Lerp(prevCamY, camY, TickDelta);
            var e = camT.eulerAngles;
            camT.rotation = Quaternion.Euler(e.x, e.y, 0f); // Slime Rancher's camera roll springs are not Minecraft's
        }

        private void OnPreCull() { Perf.Run("View.PreCull", PreCull); }

        private void PreCull()
        {
            if (!Game.Active || camT == null || SrWorld.Player == null) return;
            if (Motor.SuspendedByGame) { HideFp(true); if (Body != null) Body.SetVisible(false, false); return; }
            Restore();
            savedPos = camT.localPosition;
            savedRot = camT.localRotation;
            applied = true;

            float td = TickDelta;
            float pitch = Pitch;
            var B = Matrix4x4.identity;
            // GameRenderer.tiltViewWhenHurt
            if (Health.HurtTime > 0)
            {
                float g = (Health.HurtTime - td) / 10f;
                if (g > 0) { g = Mathf.Sin(g * g * g * g * Mathf.PI); B = B * Matrix4x4.Rotate(Quaternion.AngleAxis(-g * 14f, Vector3.forward)); }
            }
            // GameRenderer.bobView
            float walk = hSpeed - prevHSpeed;
            float gg = -(hSpeed + walk * td);
            float h = Mathf.Lerp(prevStride, stride, td);
            B = B * Matrix4x4.Translate(new Vector3(Mathf.Sin(gg * Mathf.PI) * h * 0.5f, -Mathf.Abs(Mathf.Cos(gg * Mathf.PI) * h), 0));
            B = B * Matrix4x4.Rotate(Quaternion.AngleAxis(Mathf.Sin(gg * Mathf.PI) * h * 3f, Vector3.forward));
            B = B * Matrix4x4.Rotate(Quaternion.AngleAxis(Mathf.Abs(Mathf.Cos(gg * Mathf.PI - 0.2f) * h) * 5f, Vector3.right));
            var S = Matrix4x4.Scale(new Vector3(1, 1, -1));
            var camDelta = S * B.inverse * S;

            // eye: the plain eye placed after Slime Rancher's camera update, then F5
            var eye = camT.position;
            var rot = camT.rotation;
            var camPos = eye;
            var camRot = rot;
            if (Perspective != 0)
            {
                if (Perspective == 2) camRot = rot * Quaternion.Euler(0, 180, 0) * Quaternion.Euler(-2 * pitch, 0, 0);
                if (Perspective == 2) camRot = Quaternion.Euler(-pitch, rot.eulerAngles.y + 180f, 0);
                var back = camRot * Vector3.back;
                float dist = 4f;
                // Camera.clipToSpace: eight probes around the eye, nearest hit wins
                if (Physics.SphereCast(eye, 0.1f, back, out var hit, 4f, SrWorld.SolidMask, QueryTriggerInteraction.Ignore))
                    dist = Mathf.Max(0.1f, hit.distance - 0.05f);
                camPos = eye + back * dist;
            }
            var worldPos = camPos + camRot * (Vector3)camDelta.GetColumn(3);
            var worldRot = camRot * camDelta.rotation;
            camT.SetPositionAndRotation(worldPos, worldRot);

            if (cam != null)
            {
                if (baseFov < 0 || Mathf.Abs(cam.fieldOfView - lastSetFov) > 0.01f) baseFov = cam.fieldOfView;
                lastSetFov = baseFov * Mathf.Lerp(prevFovMul, fovMul, td);
                if (Motor.Submerged && !Motor.Swimming) { } // Minecraft narrows the FOV underwater by 6/7 only with the water fog setting
                cam.fieldOfView = lastSetFov;
            }

            if (Body != null)
            {
                Body.SetVisible(Perspective != 0, true);
                Body.Pose(td, Mathf.Lerp(prevSwing, swing, td), pitch, Vector3.zero);
            }
            bool fp = Perspective == 0 && !Screens.HideHand;
            HideFp(!fp);
            if (fp) PoseHand(B, td, pitch);
        }

        private void HideFp(bool hide)
        {
            if (fpRoot != null && fpRoot.gameObject.activeSelf == hide) fpRoot.gameObject.SetActive(!hide);
        }

        private void OnPostRender() => Restore();

        /// <summary>Puts the camera back at the plain eye (called before logic each frame and after every render).</summary>
        public void Restore()
        {
            if (!applied || camT == null) return;
            camT.localPosition = savedPos;
            camT.localRotation = savedRot;
            if (cam != null && baseFov > 0) { cam.fieldOfView = baseFov; lastSetFov = baseFov; }
            applied = false;
        }

        // Rotations in Minecraft's matrix stack, about its own axes, in degrees.
        private static Matrix4x4 RX(float d) => Matrix4x4.Rotate(Quaternion.AngleAxis(d, Vector3.right));
        private static Matrix4x4 RY(float d) => Matrix4x4.Rotate(Quaternion.AngleAxis(d, Vector3.up));
        private static Matrix4x4 RZ(float d) => Matrix4x4.Rotate(Quaternion.AngleAxis(d, Vector3.forward));
        private static Matrix4x4 T(float x, float y, float z) => Matrix4x4.Translate(new Vector3(x, y, z));

        public static void Apply(Transform t, Matrix4x4 m)
        {
            t.localPosition = m.GetColumn(3);
            Vector3 c0 = m.GetColumn(0), c1 = m.GetColumn(1), c2 = m.GetColumn(2);
            t.localRotation = Quaternion.LookRotation(c2, c1);
            t.localScale = new Vector3(c0.magnitude, c1.magnitude, c2.magnitude);
        }

        private void PoseHand(Matrix4x4 bob, float td, float pitch)
        {
            var S = Matrix4x4.Scale(new Vector3(1, 1, -1));
            var A = Matrix4x4.Scale(-Vector3.one);            // MPart geometry is -v
            var M = bob;
            float lagP = Mathf.Lerp(prevRenderPitch, renderPitch, td), lagY = Mathf.Lerp(prevRenderYaw, renderYaw, td);
            M = M * RX((pitch - lagP) * 0.1f) * RY((Yaw - lagY) * 0.1f);
            float sw = Mathf.Lerp(prevSwing, swing, td);
            float eq = 1f - Mathf.Lerp(prevEquip, equip, td);
            const float i = 1f; // right arm

            if (shown == null || heldVisual == null)
            {
                // HeldItemRenderer.renderArmHoldingItem
                armT.gameObject.SetActive(true);
                heldRoot.gameObject.SetActive(false);
                float g = Mathf.Sqrt(sw);
                float hx = -0.3f * Mathf.Sin(g * Mathf.PI), iy = 0.4f * Mathf.Sin(g * Mathf.PI * 2f), jz = -0.4f * Mathf.Sin(sw * Mathf.PI);
                M = M * T(i * (hx + 0.64000005f), iy - 0.6f + eq * -0.6f, jz - 0.71999997f);
                M = M * RY(i * 45f);
                float k = Mathf.Sin(sw * sw * Mathf.PI), l = Mathf.Sin(g * Mathf.PI);
                M = M * RY(i * l * 70f) * RZ(i * k * -20f);
                M = M * T(i * -1f, 3.6f, 3.5f) * RZ(i * 120f) * RX(200f) * RY(i * -135f) * T(i * 5.6f, 0, 0);
                M = M * T(-5f / 16f, (slim ? 2.5f : 2f) / 16f, 0);
                Apply(armT, S * M * A);
                toolMatrix = S * M * A;
                return;
            }

            armT.gameObject.SetActive(false);
            heldRoot.gameObject.SetActive(true);
            string action = Interaction.Using ? Interaction.UseAction : null;
            if (action == "eat" || action == "drink")
            {
                // applyEatOrDrinkTransformation
                float f = Interaction.UseTicksLeft - td + 1f;
                float g = f / Interaction.UseMax;
                if (g < 0.8f) M = M * T(0, Mathf.Abs(Mathf.Cos(f / 4f * Mathf.PI) * 0.1f), 0);
                float hh = 1f - Mathf.Pow(g, 27f);
                M = M * T(hh * 0.6f * i, hh * -0.5f, 0) * RY(i * hh * 90f) * RX(hh * 10f) * RZ(i * hh * 30f);
                M = M * T(i * 0.56f, -0.52f + eq * -0.6f, -0.72f);
            }
            else if (action == "bow" || action == "spear" || action == "crossbow")
            {
                M = M * T(i * 0.56f, -0.52f + eq * -0.6f, -0.72f);
                float m = Interaction.UseElapsed + td;
                if (action == "bow")
                {
                    M = M * T(i * -0.2785682f, 0.18344387f, 0.15731531f) * RX(-13.935f) * RY(i * 35.3f) * RZ(i * -9.785f);
                    float f = m / 20f; f = (f * f + f * 2f) / 3f; if (f > 1f) f = 1f;
                    if (f > 0.1f) { float j = Mathf.Sin((m - 0.1f) * 1.3f) * (f - 0.1f); M = M * T(0, j * 0.004f, 0); }
                    M = M * T(0, 0, f * 0.04f) * Matrix4x4.Scale(new Vector3(1, 1, 1 + f * 0.2f)) * RY(-i * 45f);
                }
                else if (action == "spear")
                {
                    M = M * T(i * -0.5f, 0.7f, 0.1f) * RX(-55f) * RY(i * 35.3f) * RZ(i * -9.785f);
                    float f = m / 10f; if (f > 1f) f = 1f;
                    if (f > 0.1f) { float j = Mathf.Sin((m - 0.1f) * 1.3f) * (f - 0.1f); M = M * T(0, j * 0.004f, 0); }
                    M = M * T(0, 0, f * 0.2f) * Matrix4x4.Scale(new Vector3(1, 1, 1 + f * 0.2f)) * RY(-i * 45f);
                }
                else
                {
                    M = M * T(i * -0.4785682f, -0.094387f, 0.05731531f) * RX(-11.935f) * RY(i * 65.3f) * RZ(i * -9.785f);
                    float f = Mathf.Min(1f, m / Interaction.CrossbowPullTime(shown));
                    if (f > 0.1f) { float j = Mathf.Sin((m - 0.1f) * 1.3f) * (f - 0.1f); M = M * T(0, j * 0.004f, 0); }
                    M = M * T(0, 0, f * 0.04f) * Matrix4x4.Scale(new Vector3(1, 1, 1 + f * 0.2f)) * RY(-i * 45f);
                }
            }
            else if (action == "block")
            {
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
                if (shown.Is("crossbow") && Interaction.CrossbowCharged(shown) && sw < 0.001f) M = M * T(i * -0.641864f, 0, 0) * RY(i * 10f);
            }
            // our item meshes are already mirrored into Unity space, so the conversion is S on both sides;
            // the model's display transform is on the visual's own root
            Apply(heldRoot, S * M * S);
        }

        private Matrix4x4 toolMatrix;

        public void ForceRebuild() { shownKey = "~"; }
    }

    [HarmonyLib.HarmonyPatch]
    internal static class ViewPatches
    {
        // Slime Rancher's camera follows its own controller; Minecraft's eye is placed right after it, every frame.
        [HarmonyLib.HarmonyPatch(typeof(vp_FPCamera), "LateUpdate"), HarmonyLib.HarmonyPostfix]
        private static void AfterCamera(vp_FPCamera __instance)
        {
            if (Game.Active && View.I != null && __instance is SRFPCamera) View.I.PlaceEye();
        }
    }
}
