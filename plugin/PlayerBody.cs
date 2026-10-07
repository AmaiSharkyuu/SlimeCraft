using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Amai standing where the player is: Minecraft's PlayerEntityModel (slim arms, outer skin layers) animated by
    /// BipedEntityModel.setAngles (walking limbs, head look, sneaking, arm swing, idle sway, item / bow / spear
    /// poses, the swimming stroke) and PlayerEntityRenderer.setupTransforms (swim lean, elytra gliding), wearing
    /// Minecraft armour, the elytra and holding the current item. Seen in F5 and casting the player's shadow.
    /// </summary>
    public class PlayerBody : MonoBehaviour
    {
        private EntityModelInstance model;
        private MPart head, body, rArm, lArm, rLeg, lLeg;
        private MPart elytraModel; private Transform elytraT;
        public Transform HandAnchor;
        private GameObject held; private string heldKey = "";
        private readonly Dictionary<string, GameObject> armor = new Dictionary<string, GameObject>();
        private string armorKey = "";
        private Renderer[] renderers;
        private bool slim;

        // limb animation (LimbAnimator) and body yaw (LivingEntity.turnHead)
        private float limbPos, limbSpeed, prevLimbSpeed;
        private float bodyYaw, prevBodyYaw;
        private int age;

        public static PlayerBody Create(Texture2D skin, bool slim)
        {
            var go = new GameObject("SubnautiCraft Amai");
            var b = go.AddComponent<PlayerBody>();
            b.slim = slim;
            var root = Models.Player(slim);
            b.model = EntityModelInstance.Create(root, skin, go.transform, 0);
            b.head = root.Find("head"); b.body = root.Find("body");
            b.rArm = root.Find("right_arm"); b.lArm = root.Find("left_arm");
            b.rLeg = root.Find("right_leg"); b.lLeg = root.Find("left_leg");
            b.HandAnchor = new GameObject("hand").transform;
            b.HandAnchor.SetParent(b.rArm.T, false);
            // HeldItemFeatureRenderer: rotX(-90) rotY(180) translate(1/16, 0.125, -0.625) in the arm's space,
            // converted: arm part space is -v, item meshes are mirrored z (S)
            var K = Matrix4x4.Rotate(Quaternion.AngleAxis(-90, Vector3.right)) * Matrix4x4.Rotate(Quaternion.AngleAxis(180, Vector3.up)) * Matrix4x4.Translate(new Vector3(1f / 16f, 0.125f, -0.625f));
            View.Apply(b.HandAnchor, Matrix4x4.Scale(-Vector3.one) * K * Matrix4x4.Scale(new Vector3(1, 1, -1)));
            // elytra: ElytraFeatureRenderer draws it from the model root, translated 2 px back (not with the body)
            b.elytraModel = Models.Elytra();
            var eh = new GameObject("elytra");
            eh.transform.SetParent(b.model.Go.transform.GetChild(0), false);
            eh.transform.localPosition = -new Vector3(0, 0, 2f) / 16f;
            b.elytraModel.Build(eh.transform, BlockWorld.MakeMat(1, McTex.Get("entity/elytra")), 64, 32, 0);
            b.elytraT = eh.transform;
            b.renderers = go.GetComponentsInChildren<Renderer>(true);
            Sky.Apply(go);
            return b;
        }

        public void SetVisible(bool full, bool shadow)
        {
            renderers = GetComponentsInChildren<Renderer>(true);
            // Subnautica's renderer draws "shadows only" objects anyway, so in first person the body is simply off
            foreach (var r in renderers)
            {
                if (r.enabled != full) r.enabled = full;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }
        }

        /// <summary>20 Hz: limbs and body yaw.</summary>
        public void Tick()
        {
            age++;
            prevLimbSpeed = limbSpeed;
            float moved = Motor.Swimming || Motor.FallFlying ? Motor.Vel.magnitude : Motor.HorizontalTickSpeed;
            limbSpeed += (Mathf.Min(moved * 4f, 1f) - limbSpeed) * 0.4f;
            limbPos += limbSpeed;
            // LivingEntity.turnHead: the body turns towards the movement, never more than 50 degrees from the head
            prevBodyYaw = bodyYaw;
            float headYaw = View.I != null ? View.I.Yaw : 0;
            var v = Motor.Vel;
            if (new Vector2(v.x, v.z).sqrMagnitude > 0.0025f && !Motor.Swimming)
            {
                float moveYaw = Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
                if (Mathf.Abs(Mathf.DeltaAngle(moveYaw, headYaw)) > 95f) moveYaw += 180f; // walking backwards
                bodyYaw += Mathf.DeltaAngle(bodyYaw, moveYaw) * 0.3f;
            }
            float diff = Mathf.DeltaAngle(bodyYaw, headYaw);
            if (Mathf.Abs(diff) > 50f) bodyYaw += diff - Mathf.Sign(diff) * 50f;
            if (Motor.Swimming || Motor.FallFlying || Interaction.Using) bodyYaw = headYaw;
        }

        private static float LerpAngle(float t, float a, float b)
        {
            float f = (b - a) % (Mathf.PI * 2f);
            if (f < -Mathf.PI) f += Mathf.PI * 2f;
            if (f >= Mathf.PI) f -= Mathf.PI * 2f;
            return a + t * f;
        }

        private static float M2807(float f) => -65f * f + f * f;

        /// <summary>Every frame with interpolated state.</summary>
        public void Pose(float td, float swing, float pitchDeg, Vector3 eyeOffset)
        {
            var p = Player.main;
            if (p == null) return;
            UpdateHeld();
            UpdateArmor();
            var feet = p.transform.position + eyeOffset - Vector3.up * Motor.EyeHeight;
            float headYaw = View.I != null ? View.I.Yaw : 0;
            float by = Mathf.LerpAngle(prevBodyYaw, bodyYaw, td);
            float lean = Mathf.Lerp(Motor.PrevSwimAmount, Motor.SwimAmount, td);
            float anim = age + td;
            float f = limbPos - limbSpeed * (1f - td);
            float g = Mathf.Min(1f, Mathf.Lerp(prevLimbSpeed, limbSpeed, td));
            float netHead = Mathf.DeltaAngle(by, headYaw);
            bool gliding = Motor.FallFlying;
            bool swimPose = Motor.Swimming;

            // ---- setupTransforms: root at the feet, turned to the body yaw, leaning while swimming / gliding
            var tr = transform;
            tr.position = feet;
            var rot = Quaternion.Euler(0, by, 0);
            Vector3 extraOffset = Vector3.zero;
            if (gliding)
            {
                float j = Motor.FallFlyTicks + td;
                float k = Mathf.Clamp01(j * j / 100f);
                rot = rot * Quaternion.AngleAxis(-(k * (-90f - pitchDeg)), Vector3.right);
                // roll into turns: the angle between where we look and where we fly
                var look = MainCamera.camera.transform.forward; var vel = Motor.Vel;
                float d = vel.x * vel.x + vel.z * vel.z, e = look.x * look.x + look.z * look.z;
                if (d > 0 && e > 0)
                {
                    float l = Mathf.Clamp((vel.x * look.x + vel.z * look.z) / Mathf.Sqrt(d * e), -1, 1);
                    float m = vel.x * look.z - vel.z * look.x;
                    rot = rot * Quaternion.AngleAxis(-Mathf.Sign(m) * Mathf.Acos(l) * Mathf.Rad2Deg, Vector3.up);
                }
            }
            else if (lean > 0)
            {
                float jx = Motor.InWater ? -90f - pitchDeg : -90f;
                float kx = Mathf.Lerp(0f, jx, lean);
                rot = rot * Quaternion.AngleAxis(-kx, Vector3.right);
                if (swimPose) extraOffset = new Vector3(0, -1f, -0.3f);
            }
            tr.rotation = rot;
            tr.position = feet + rot * extraOffset;
            tr.localScale = Vector3.one * 0.9375f;

            // ---- BipedEntityModel.setAngles
            float headPitch = pitchDeg * Mathf.Deg2Rad;
            float hp;
            if (gliding) hp = -Mathf.PI / 4f;
            else if (lean > 0) hp = swimPose ? LerpAngle(lean, headPitch, -Mathf.PI / 4f) : LerpAngle(lean, headPitch, headPitch);
            else hp = headPitch;
            float hy = netHead * Mathf.Deg2Rad;

            float kk = 1f;
            if (gliding) { kk = Motor.Vel.sqrMagnitude / 0.2f; kk *= kk * kk; }
            if (kk < 1f) kk = 1f;
            float rap = Mathf.Cos(f * 0.6662f + Mathf.PI) * 2f * g * 0.5f / kk, lap = Mathf.Cos(f * 0.6662f) * 2f * g * 0.5f / kk;
            float rar = 0, lar = 0, ray = 0, lay = 0;
            float rlp = Mathf.Cos(f * 0.6662f) * 1.4f * g / kk, llp = Mathf.Cos(f * 0.6662f + Mathf.PI) * 1.4f * g / kk;
            bool riding = Motor.SuspendedByGame && p.GetMode() != Player.Mode.Normal;
            if (riding) { rap += -Mathf.PI / 5f; lap += -Mathf.PI / 5f; rlp = -1.4137167f; llp = -1.4137167f; }

            // arm pose from the held item / use action
            var heldStack = Inventory.Held;
            string pose = heldStack == null ? "empty" : "item";
            if (Interaction.Using)
            {
                switch (Interaction.UseAction) { case "bow": pose = "bow"; break; case "spear": pose = "spear"; break; case "block": pose = "block"; break; case "crossbow": pose = "crossbow_charge"; break; }
            }
            switch (pose)
            {
                case "block": rap = rap * 0.5f - 0.9424779f; ray = -Mathf.PI / 6f; break;
                case "item": rap = rap * 0.5f - Mathf.PI / 10f; ray = 0; break;
                case "spear": rap = rap * 0.5f - Mathf.PI; ray = 0; break;
                case "bow": ray = -0.1f + hy; lay = 0.1f + hy + 0.4f; rap = -Mathf.PI / 2f + hp; lap = -Mathf.PI / 2f + hp; break;
                case "crossbow_charge": ray = -0.8f; rap = -0.97079635f; lap = -0.97079635f; lay = 0.4f; break;
            }

            // animateArms (attack swing)
            float bodyY = 0;
            Vector3 rArmPivot = new Vector3(-5, 2, 0), lArmPivot = new Vector3(5, 2, 0);
            if (swing > 0)
            {
                bodyY = Mathf.Sin(Mathf.Sqrt(swing) * Mathf.PI * 2f) * 0.2f;
                rArmPivot.z = Mathf.Sin(bodyY) * 5f; rArmPivot.x = -Mathf.Cos(bodyY) * 5f;
                lArmPivot.z = -Mathf.Sin(bodyY) * 5f; lArmPivot.x = Mathf.Cos(bodyY) * 5f;
                ray += bodyY; lay += bodyY; lap += bodyY;
                float ff = 1f - swing; ff *= ff; ff *= ff; ff = 1f - ff;
                float gs = Mathf.Sin(ff * Mathf.PI);
                float h = Mathf.Sin(swing * Mathf.PI) * -(hp - 0.7f) * 0.75f;
                rap -= gs * 1.2f + h;
                ray += bodyY * 2f;
                rar += Mathf.Sin(swing * Mathf.PI) * -0.4f;
            }

            bool sneak = Motor.Sneaking && !riding;
            float bodyP = 0;
            Vector3 headPivot = Vector3.zero, bodyPivot = Vector3.zero, rLegPivot = new Vector3(-1.9f, 12, 0), lLegPivot = new Vector3(1.9f, 12, 0);
            if (sneak)
            {
                bodyP = 0.5f; rap += 0.4f; lap += 0.4f;
                rLegPivot.z = 4; lLegPivot.z = 4; rLegPivot.y = 12.2f; lLegPivot.y = 12.2f;
                headPivot.y = 4.2f; bodyPivot.y = 3.2f; lArmPivot.y = 5.2f; rArmPivot.y = 5.2f;
            }
            // CrossbowPosing.swingArm: idle sway
            rar += 1f * (Mathf.Cos(anim * 0.09f) * 0.05f + 0.05f); rap += 1f * Mathf.Sin(anim * 0.067f) * 0.05f;
            lar += -1f * (Mathf.Cos(anim * 0.09f) * 0.05f + 0.05f); lap += -1f * Mathf.Sin(anim * 0.067f) * 0.05f;

            if (lean > 0)
            {
                float l = f % 26f;
                float m = swing > 0 ? 0 : lean, n = lean;
                if (!Interaction.Using)
                {
                    if (l < 14f)
                    {
                        lap = LerpAngle(n, lap, 0); rap = Mathf.Lerp(rap, 0, m);
                        lay = LerpAngle(n, lay, Mathf.PI); ray = Mathf.Lerp(ray, Mathf.PI, m);
                        lar = LerpAngle(n, lar, Mathf.PI + 1.8707964f * M2807(l) / M2807(14f));
                        rar = Mathf.Lerp(rar, Mathf.PI - 1.8707964f * M2807(l) / M2807(14f), m);
                    }
                    else if (l < 22f)
                    {
                        float o = (l - 14f) / 8f;
                        lap = LerpAngle(n, lap, Mathf.PI / 2f * o); rap = Mathf.Lerp(rap, Mathf.PI / 2f * o, m);
                        lay = LerpAngle(n, lay, Mathf.PI); ray = Mathf.Lerp(ray, Mathf.PI, m);
                        lar = LerpAngle(n, lar, 5.012389f - 1.8707964f * o); rar = Mathf.Lerp(rar, 1.2707963f + 1.8707964f * o, m);
                    }
                    else
                    {
                        float o = (l - 22f) / 4f;
                        lap = LerpAngle(n, lap, Mathf.PI / 2f - Mathf.PI / 2f * o); rap = Mathf.Lerp(rap, Mathf.PI / 2f - Mathf.PI / 2f * o, m);
                        lay = LerpAngle(n, lay, Mathf.PI); ray = Mathf.Lerp(ray, Mathf.PI, m);
                        lar = LerpAngle(n, lar, Mathf.PI); rar = Mathf.Lerp(rar, Mathf.PI, m);
                    }
                }
                llp = Mathf.Lerp(llp, 0.3f * Mathf.Cos(f * 0.33333334f + Mathf.PI), lean);
                rlp = Mathf.Lerp(rlp, 0.3f * Mathf.Cos(f * 0.33333334f), lean);
            }

            if (slim) { rArmPivot.y += 0.5f; lArmPivot.y += 0.5f; }
            head.PoseAt(headPivot, hp, hy, 0);
            body.PoseAt(bodyPivot, bodyP, bodyY, 0);
            rArm.PoseAt(rArmPivot, rap, ray, rar);
            lArm.PoseAt(lArmPivot, lap, lay, lar);
            rLeg.PoseAt(rLegPivot, rlp, riding ? Mathf.PI / 10f : 0.005f, riding ? 0.07853982f : 0.005f);
            lLeg.PoseAt(lLegPivot, llp, riding ? -Mathf.PI / 10f : -0.005f, riding ? -0.07853982f : -0.005f);

            // ---- elytra (ElytraEntityModel.setAngles)
            bool wings = Elytra.Worn;
            if (elytraT.gameObject.activeSelf != wings) elytraT.gameObject.SetActive(wings);
            if (wings)
            {
                float ek = Mathf.PI / 12f, el = -Mathf.PI / 12f, em = 0, en = 0;
                if (gliding)
                {
                    float o = 1f;
                    var vel = Motor.Vel;
                    if (vel.y < 0) o = 1f - Mathf.Pow(-vel.normalized.y, 1.5f);
                    ek = o * (Mathf.PI / 9f) + (1f - o) * ek;
                    el = o * (-Mathf.PI / 2f) + (1f - o) * el;
                }
                else if (sneak) { ek = Mathf.PI * 2f / 9f; el = -Mathf.PI / 4f; em = 3f; en = 0.08726646f; }
                elytraModel.Find("left_wing").PoseAt(new Vector3(5, em, 0), ek, en, el);
                elytraModel.Find("right_wing").PoseAt(new Vector3(-5, em, 0), ek, -en, -el);
            }
        }

        private void UpdateHeld()
        {
            var s = Inventory.Held;
            var key = s == null ? "" : s.Id + s.Damage + s.Potion + s.Color + (s.IsSn && SnModels.Ready(s.Tech) ? "3d" : "");
            if (key == heldKey && (held != null || s == null)) return;
            heldKey = key;
            if (held != null) Destroy(held);
            held = null;
            if (s == null) return;
            held = ItemRender.Build(s, HandAnchor, 0, "thirdperson_righthand");
            Sky.Apply(gameObject);
            foreach (var r in held.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            renderers = GetComponentsInChildren<Renderer>(true);
        }

        /// <summary>ArmorFeatureRenderer: armour pieces as inflated copies of the body parts with models/armor textures.</summary>
        private void UpdateArmor()
        {
            var key = "";
            foreach (var a in Inventory.Armor) key += a == null ? "-" : a.Id + a.Color + (a.Enchanted ? "e" : "");
            if (key == armorKey) return;
            armorKey = key;
            foreach (var g in armor.Values) if (g != null) Destroy(g);
            armor.Clear();
            for (int i = 0; i < 4; i++)
            {
                var a = Inventory.Armor[i];
                if (a == null || !a.IsMc || a.Item.ArmorMaterial == null) continue;
                var matName = a.Item.ArmorMaterial;
                bool legs = a.Item.ArmorSlot == "legs";
                var tex = McTex.Get("models/armor/" + matName + "_layer_" + (legs ? 2 : 1));
                var mat = BlockWorld.MakeMat(1, tex);
                if (matName == "leather") mat.color = ItemRender.Hex(a.Color >= 0 ? a.Color : 0xA06540);
                float dil = legs ? 0.5f : 1f;
                switch (a.Item.ArmorSlot)
                {
                    case "head": AddArmor("head", head, new MPart("helmet").Box(0, 0, -4, -8, -4, 8, 8, 8, dil), mat); break;
                    case "chest":
                        AddArmor("body", body, new MPart("chest").Box(16, 16, -4, 0, -2, 8, 12, 4, dil), mat);
                        AddArmor("rarm", rArm, new MPart("rarm").Box(40, 16, -3, -2, -2, 4, 12, 4, dil), mat);
                        AddArmor("larm", lArm, new MPart("larm").Box(40, 16, -1, -2, -2, 4, 12, 4, dil, true), mat);
                        break;
                    case "legs":
                        AddArmor("belt", body, new MPart("belt").Box(16, 16, -4, 0, -2, 8, 12, 4, dil), mat);
                        AddArmor("rleg", rLeg, new MPart("rleg").Box(0, 16, -2, 0, -2, 4, 12, 4, dil), mat);
                        AddArmor("lleg", lLeg, new MPart("lleg").Box(0, 16, -2, 0, -2, 4, 12, 4, dil, true), mat);
                        break;
                    case "feet":
                        AddArmor("rboot", rLeg, new MPart("rboot").Box(0, 16, -2, 0, -2, 4, 12, 4, dil), mat);
                        AddArmor("lboot", lLeg, new MPart("lboot").Box(0, 16, -2, 0, -2, 4, 12, 4, dil, true), mat);
                        break;
                }
            }
            renderers = GetComponentsInChildren<Renderer>(true);
            Sky.Apply(gameObject);
        }

        private void AddArmor(string key, MPart onto, MPart piece, Material mat)
        {
            var holder = new GameObject("armor " + key);
            holder.transform.SetParent(onto.T, false);
            piece.Build(holder.transform, mat, 64, 32, 0);
            armor[key] = holder;
        }
    }
}
