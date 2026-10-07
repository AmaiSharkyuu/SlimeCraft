using HarmonyLib;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Minecraft's player movement, run as real 20 Hz ticks (LivingEntity.travel, PlayerEntity.travel,
    /// ClientPlayerEntity.tickMovement) on Slime Rancher's own character controller. All numbers are Minecraft's,
    /// in blocks (= metres) per tick. The camera is interpolated between ticks by View, like Minecraft does.
    ///
    ///  * ground: speed 0.1 (sprint x1.3, sneak x0.3), slipperiness of the Minecraft block underfoot, jump 0.42
    ///  * water (ponds, rivers): 0.02 accel, drag 0.8 (0.9 sprinting), sinking by gravity/16, space swims up
    ///    0.04; sprinting underwater is Minecraft's swimming pose that follows the look
    ///  * creative flight (double jump), elytra gliding (jump in the air) and firework boosts
    ///  * fall damage, fly-into-wall damage, exhaustion for the hunger bar
    /// Slime Rancher's pushes (spring pads, explosions) still reach the player as Minecraft velocity.
    /// </summary>
    public static class Motor
    {
        // state, Minecraft units
        public static Vector3 Vel;                  // blocks/tick, Unity axes
        public static Vector3 Pos, PrevPos;          // eye position at the last two ticks (for camera interpolation)
        public static bool OnGround, Sprinting, Sneaking, Flying, FallFlying, Swimming, InWater, Submerged, Moving, HorizontalCollision;
        public static float FallDistance;
        public static float EyeHeight = 1.62f, PrevEyeHeight = 1.62f;
        public static float HorizontalTickSpeed;
        public static int FallFlyTicks;
        public static int RocketTicks;
        public static bool SuspendedByGame;
        public static float SwimAmount, PrevSwimAmount;          // 0..1 swim pose blend (LivingEntity.leaningPitch)
        public static Vector3 LastInput;

        private static bool jumpWasDown, forwardWasDown;
        private static int jumpTapTicks, sprintTapTicks;
        private static float acc;
        private static int jumpCooldown;
        private static bool initialised;

        public static Vector2 TestMove; public static float TestUntil; public static bool TestJump, TestSprint, TestSneak;

        private const float Gravity = 0.08f;

        public static void Reset()
        {
            Vel = Vector3.zero;
            moveVel = Vector3.zero;
            FallDistance = 0;
            Flying = FallFlying = Swimming = false;
            initialised = false;
        }

        /// <summary>When Beatrix is dead (the respawn sequence) Slime Rancher keeps the controller.</summary>
        public static bool GameOwnsPlayer(vp_FPController c)
        {
            if (c == null) return true;
            var st = Health.State;
            return st != null && st.GetCurrHealth() <= 0;
        }

        // ------------------------------------------------------------------------------------------------ boxes

        /// <summary>Minecraft's EntityPose dimensions: standing 0.6x1.8 (eye 1.62), sneaking 1.5 (1.27), swimming/gliding 0.6 (0.4).</summary>
        private static void PoseBox(out float height, out float eye)
        {
            if (Swimming || FallFlying) { height = 0.6f; eye = 0.4f; }
            else if (Sneaking) { height = 1.5f; eye = 1.27f; }
            else { height = 1.8f; eye = 1.62f; }
        }

        private static void ApplyBox(CharacterController cc, float height, float eye)
        {
            cc.radius = 0.3f;
            cc.height = height;
            cc.center = new Vector3(0, height / 2f, 0); // Slime Rancher's player transform is at the feet
            cc.stepOffset = Mathf.Min(0.6f, height - 0.01f);
            cc.slopeLimit = 50f;
            cc.skinWidth = 0.03f;
        }

        private static bool Fits(Vector3 eyePos, float height, float eye)
        {
            var feet = eyePos - Vector3.up * eye;
            return !Physics.CheckCapsule(feet + Vector3.up * 0.31f, feet + Vector3.up * (height - 0.31f), 0.29f, SrWorld.SolidMask, QueryTriggerInteraction.Ignore);
        }

        // ------------------------------------------------------------------------------------------------ fixed step

        private static readonly AccessTools.FieldRef<vp_Controller, bool> grounded = AccessTools.FieldRefAccess<vp_Controller, bool>("m_Grounded");
        private static readonly AccessTools.FieldRef<vp_Controller, bool> groundedNonMountain = AccessTools.FieldRefAccess<vp_Controller, bool>("m_GroundedNonMountain");
        private static readonly AccessTools.FieldRef<vp_Controller, Vector3> velocityField = AccessTools.FieldRefAccess<vp_Controller, Vector3>("m_Velocity");
        private static readonly AccessTools.FieldRef<vp_Controller, float> fallSpeed = AccessTools.FieldRefAccess<vp_Controller, float>("m_FallSpeed");
        private static readonly AccessTools.FieldRef<vp_FPController, Vector3> externalForce = AccessTools.FieldRefAccess<vp_FPController, Vector3>("m_ExternalForce");
        private static readonly AccessTools.FieldRef<vp_FPController, Vector3> smoothPos = AccessTools.FieldRefAccess<vp_FPController, Vector3>("m_SmoothPosition");
        private static readonly AccessTools.FieldRef<vp_FPController, Vector3> moveDirection = AccessTools.FieldRefAccess<vp_FPController, Vector3>("m_MoveDirection");

        /// <summary>Replaces vp_FPController's motor while Minecraft owns the player (every frame).</summary>
        public static void Fixed(vp_FPController p)
        {
            var cc = p.CharacterController;
            if (!initialised)
            {
                initialised = true;
                // Slime Rancher's stamina run and jetpack are Minecraft's sprint and nothing now.
                var sr = p.GetComponent<StaminaRun>(); if (sr != null) sr.enabled = false;
                var jet = p.GetComponent<EnergyJetpack>(); if (jet != null) jet.enabled = false;
                PoseBox(out float h0, out float e0);
                ApplyBox(cc, h0, e0);
                Pos = PrevPos = p.transform.position + Vector3.up * EyeHeight;
            }

            float dt = Time.deltaTime;
            if (dt <= 0f || Time.timeScale == 0f) { smoothPos(p) = p.transform.position; return; }

            // Spring pads, explosions and other pushes from Slime Rancher become Minecraft velocity.
            ref var ext = ref externalForce(p);
            if (ext.sqrMagnitude > 1e-6f) { Vel += ext * 3f; ext = Vector3.zero; } // per-frame at 60 fps -> blocks per tick

            // Minecraft moves by the tick's velocity once per tick; spreading that move over the frames of the tick
            // covers exactly the same distance, but smoothly at the screen's frame rate.
            FrameMove(p, cc, dt);
            acc += dt;
            int guard = 0;
            while (acc >= 0.05f && guard++ < 5)
            {
                acc -= 0.05f;
                HorizontalTickSpeed = new Vector2(tickDisp.x, tickDisp.z).magnitude;
                Moving = HorizontalTickSpeed > 0.003f;
                Stats.Moved(tickDisp);
                tickDisp = Vector3.zero;
                Tick(p, cc);
                HorizontalCollision = false;
            }
            if (guard >= 5) acc = 0;

            // Tell Slime Rancher what happened, for everything that reads its controller.
            smoothPos(p) = p.transform.position;
            grounded(p) = OnGround;
            groundedNonMountain(p) = OnGround;
            velocityField(p) = moveVel * 20f;
            fallSpeed(p) = 0;
            moveDirection(p) = Vector3.zero;
        }

        /// <summary>Minecraft's keys by their printed letters: ZQSD on an AZERTY keyboard, WASD otherwise, arrows, or a gamepad.</summary>
        private static Vector2 MoveKeys()
        {
            bool az = Game.DropKey == KeyCode.A;
            float f = (Input.GetKey(az ? KeyCode.Z : KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1 : 0) - (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1 : 0);
            float r = (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1 : 0) - (Input.GetKey(az ? KeyCode.Q : KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1 : 0);
            if (f != 0 || r != 0) return new Vector2(r, f);
            if (!InputDirector.UsingGamepad()) return Vector2.zero;
            return new Vector2(SRInput.Actions.horizontal, SRInput.Actions.vertical);
        }

        private static Vector3 moveVel, tickDisp;

        /// <summary>How far we are between the last 20 Hz tick and the next, counting the time since the last physics step.</summary>
        public static float Partial => Mathf.Clamp01(acc / 0.05f);

        private static void Tick(vp_FPController p, CharacterController cc)
        {
            PrevPos = p.transform.position + Vector3.up * EyeHeight;
            PrevEyeHeight = EyeHeight;
            PrevSwimAmount = SwimAmount;
            bool paused = Screens.Open || Chat.Open || SRInput.Instance.GetInputMode() != SRInput.InputMode.DEFAULT;

            // ---- input
            Vector2 move = Vector2.zero;
            bool jump = false, sneakKey = false, sprintKey = false;
            if (!paused)
            {
                move = MoveKeys();
                jump = SRInput.Actions.jump.IsPressed || Input.GetKey(KeyCode.Space);
                sneakKey = Input.GetKey(KeyCode.LeftShift) || TestSneak;
                sprintKey = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            }
            if (Time.time < TestUntil) { move = TestMove; jump |= TestJump; sprintKey |= TestSprint; }
            bool forward = move.y > 0.8f;

            // ---- water (Minecraft's isTouchingWater / isSubmergedInWater against Slime Rancher's ponds)
            var eyePos = p.transform.position + Vector3.up * EyeHeight;
            float level = SrWorld.WaterLevelAt(p.transform.position + Vector3.up * 0.1f);
            PoseBox(out float boxH, out float eyeH);
            float feetY = eyePos.y - eyeH;
            InWater = level > feetY + 0.001f;
            Submerged = level > eyePos.y + 0.11f;

            // ---- creative flight toggle: double tap jump within 7 ticks
            if (jump && !jumpWasDown)
            {
                if (GameMode.Creative)
                {
                    if (jumpTapTicks > 0) { Flying = !Flying; jumpTapTicks = 0; }
                    else jumpTapTicks = 7;
                }
                // elytra: jump while falling through the air (ClientPlayerEntity.checkFallFlying)
                if (!Flying && !OnGround && !FallFlying && !InWater && Vel.y < 0.0f && Elytra.CanGlide())
                {
                    FallFlying = true;
                    FallFlyTicks = 0;
                    Sounds.Play("item.elytra.flying", null, 0.6f);
                }
            }
            jumpWasDown = jump;
            if (jumpTapTicks > 0) jumpTapTicks--;
            if (!GameMode.Creative) Flying = false;
            if (Flying && OnGround && !jump) Flying = false;
            if (FallFlying && (OnGround || InWater || Flying || !Elytra.CanGlide())) FallFlying = false;

            // ---- sneaking (and standing back up only where there is room)
            bool wantSneak = sneakKey && !Flying && !Swimming && !FallFlying;
            if (!wantSneak && Sneaking && !Fits(eyePos + Vector3.up * (1.62f - 1.27f), 1.8f, 1.62f)) wantSneak = true;
            Sneaking = wantSneak;

            // ---- sprinting (Ctrl or double tap forward; needs food > 6 like Minecraft, never backwards)
            if (forward && !forwardWasDown)
            {
                if (sprintTapTicks > 0) sprintKey = true;
                sprintTapTicks = 7;
            }
            forwardWasDown = forward;
            if (sprintTapTicks > 0) sprintTapTicks--;
            bool canSprint = (Hunger.Food > 6 || GameMode.Creative || Flying) && !Effects.Has("blindness");
            if (!Sprinting && sprintKey && forward && canSprint && !Sneaking && (!InWater || Submerged)) Sprinting = true;
            if (Sprinting && (!forward || !canSprint || HorizontalCollision && !Swimming || Sneaking || (InWater && !Submerged && !Swimming))) Sprinting = false;

            // ---- swimming pose (PlayerEntity.updateSwimming)
            if (Swimming) Swimming = Sprinting && InWater && !Flying;
            else Swimming = Sprinting && Submerged && !Flying;
            SwimAmount = Mathf.MoveTowards(SwimAmount, Swimming || FallFlying ? 1f : 0f, Swimming || FallFlying ? 0.09f : 0.15f);

            // ---- box for the pose
            PoseBox(out boxH, out eyeH);
            if (cc.height != boxH || Mathf.Abs(EyeHeight - eyeH) > 0.001f)
            {
                // The feet stay where they are (they are the transform); the eye moves with the pose.
                var feet = p.transform.position;
                if (Fits(feet + Vector3.up * eyeH, boxH, eyeH) || boxH < cc.height)
                {
                    EyeHeight = eyeH;
                    ApplyBox(cc, boxH, eyeH);
                }
            }
            else ApplyBox(cc, boxH, eyeH);

            // ---- movement input, Minecraft style (sideways, forward) * 0.98, sneaking x0.3
            var input = new Vector3(move.x, 0, move.y) * 0.98f;
            if (Sneaking) input *= 0.3f + 0.15f * Enchants.Level(Inventory.ArmorIn("legs"), "swift_sneak");
            LastInput = input;
            var cam = Camera.main.transform;
            float yaw = cam.eulerAngles.y;
            var look = cam.forward;

            if (Flying)
            {
                if (Sneaking == false && sneakKey && !paused) Vel.y -= 0.05f * 3f;
                if (jump && !paused) Vel.y += 0.05f * 3f;
            }
            if (jumpCooldown > 0) jumpCooldown--;

            // ================================================================================== travel
            float speedAttr = 0.1f * (Sprinting ? 1.3f : 1f) * Effects.SpeedMultiplier();
            if (FallFlying)
            {
                TravelGlide(look, cc, p);
            }
            else if (InWater && !Flying)
            {
                // PlayerEntity.travel: swimming follows the look vertically
                if (Swimming)
                {
                    float d = look.y;
                    float e = d < -0.2f ? 0.085f : 0.06f;
                    bool fluidAbove = SrWorld.WaterLevelAt(eyePos) > eyePos.y + 1f;
                    if (d <= 0f || jump || fluidAbove) Vel.y += (d - Vel.y) * e;
                }
                float f = Sprinting ? 0.9f : 0.8f;
                float g = 0.02f;
                float h = Mathf.Min(3f, Enchants.DepthStrider());
                if (!OnGround) h *= 0.5f;
                if (h > 0f) { f += (0.546f - f) * h / 3f; g += (speedAttr - g) * h / 3f; }
                if (Effects.Has("dolphins_grace")) f = 0.96f;
                UpdateVelocity(g, input, yaw);
                if (jump && !Swimming) Vel.y += 0.04f; // LivingEntity.swimUpward
                if (sneakKey && !Swimming && !Sneaking) Vel.y -= 0.04f; // Minecraft 1.13+: sneaking sinks faster
                moveVel = Vel;
                if (HorizontalCollision && Climbing(p)) Vel.y = 0.2f;
                Vel = new Vector3(Vel.x * f, Vel.y * 0.8f, Vel.z * f);
                // applyFluidMovingSpeed
                if (!Sprinting)
                {
                    bool falling = Vel.y <= 0f;
                    if (falling && Mathf.Abs(Vel.y - 0.005f) >= 0.003f && Mathf.Abs(Vel.y - Gravity / 16f) < 0.003f) Vel.y = -0.003f;
                    else Vel.y -= Gravity / 16f;
                }
                // hop out of the water onto a ledge
                if (HorizontalCollision && Fits(eyePos + new Vector3(Vel.x, Vel.y + 0.6f, Vel.z), boxH, eyeH)) Vel.y = 0.3f;
                FallDistance = 0;
            }
            else
            {
                // ---- ground / air / creative flight (LivingEntity.travel last branch + PlayerEntity flying)
                var under = SrWorld.BlockUnder(p.transform.position);
                float slip = under != null ? under.Block.Slipperiness : 0.6f;
                float friction = OnGround ? slip * 0.91f : 0.91f;
                float accel;
                if (Flying) accel = 0.05f * (Sprinting ? 2f : 1f);
                else if (OnGround) accel = speedAttr * (0.21600002f / (slip * slip * slip));
                else accel = Sprinting ? 0.026f : 0.02f;

                // jumping (LivingEntity.jump): 0.42 * block jump factor + jump boost; sprint jump boost 0.2 forward
                if (jump && OnGround && !Flying && jumpCooldown == 0 && !paused)
                {
                    float jv = 0.42f * (under != null ? under.Block.JumpVelocity : 1f) + Effects.JumpBoost();
                    Vel.y = jv;
                    if (Sprinting)
                    {
                        float yr = yaw * Mathf.Deg2Rad;
                        Vel += new Vector3(Mathf.Sin(yr), 0, Mathf.Cos(yr)) * 0.2f;
                        Hunger.Exhaust(0.2f);
                    }
                    else Hunger.Exhaust(0.05f);
                    OnGround = false;
                    jumpCooldown = 10;
                    Stats.Jumped();
                }
                UpdateVelocity(accel, input, yaw);
                if (Climbing(p))
                {
                    // LivingEntity.applyClimbingSpeed: clamp horizontal, cap falling, sneaking holds on
                    Vel.x = Mathf.Clamp(Vel.x, -0.15f, 0.15f);
                    Vel.z = Mathf.Clamp(Vel.z, -0.15f, 0.15f);
                    Vel.y = Mathf.Max(Vel.y, -0.15f);
                    if (Vel.y < 0 && Sneaking) Vel.y = 0;
                    FallDistance = 0;
                }
                moveVel = Vel;
                if ((HorizontalCollision || jump) && Climbing(p)) Vel.y = 0.2f;
                float vy = Vel.y;
                if (Effects.Has("levitation")) vy += (0.05f * (Effects.Amplifier("levitation") + 1) - vy) * 0.2f;
                else if (!Flying) vy -= (Vel.y <= 0 && Effects.Has("slow_falling")) ? 0.01f : Gravity;
                if (Flying) Vel = new Vector3(Vel.x * friction, Vel.y * 0.6f, Vel.z * friction);
                else Vel = new Vector3(Vel.x * friction, vy * 0.98f, Vel.z * friction);
                if (Flying || Effects.Has("slow_falling") && Vel.y < 0) FallDistance = 0;
            }

            // ---- rockets attached to a gliding player (FireworkRocketEntity.tick)
            if (RocketTicks > 0)
            {
                RocketTicks--;
                if (FallFlying)
                {
                    var l = look;
                    Vel += l * 0.1f + (l * 1.5f - Vel) * 0.5f;
                }
                else RocketTicks = 0;
            }

            Footsteps(p);
        }

        /// <summary>Entity.updateVelocity: input rotated by yaw, normalised when longer than 1, times speed.</summary>
        private static void UpdateVelocity(float speed, Vector3 input, float yawDeg)
        {
            float len = input.sqrMagnitude;
            if (len < 1.0E-7f) return;
            var v = (len > 1f ? input.normalized : input) * speed;
            var r = Quaternion.Euler(0, yawDeg, 0) * v;
            Vel += r;
        }

        private static bool Climbing(vp_FPController p)
        {
            var feet = p.transform.position;
            var s = BlockWorld.Get(BlockWorld.CellAt(feet + Vector3.up * 0.1f));
            return s != null && s.Block.Tags.Contains("climbable");
        }

        /// <summary>LivingEntity.travel, fall-flying branch, unchanged.</summary>
        private static void TravelGlide(Vector3 look, CharacterController cc, vp_FPController p)
        {
            if (Vel.y > -0.5f) FallDistance = 1f;
            float pitch = -Mathf.Asin(Mathf.Clamp(look.y, -1, 1)); // Minecraft pitch: positive looking down
            float i = Mathf.Sqrt(look.x * look.x + look.z * look.z);
            float j = new Vector2(Vel.x, Vel.z).magnitude;
            float k = look.magnitude;
            float l = Mathf.Cos(pitch);
            l = l * l * Mathf.Min(1f, k / 0.4f);
            float d = Vel.y <= 0 && Effects.Has("slow_falling") ? 0.01f : Gravity;
            Vel += new Vector3(0, d * (-1f + l * 0.75f), 0);
            if (Vel.y < 0 && i > 0)
            {
                float m = Vel.y * -0.1f * l;
                Vel += new Vector3(look.x * m / i, m, look.z * m / i);
            }
            if (pitch < 0 && i > 0)
            {
                float m = j * -Mathf.Sin(pitch) * 0.04f;
                Vel += new Vector3(-look.x * m / i, m * 3.2f, -look.z * m / i);
            }
            if (i > 0) Vel += new Vector3((look.x / i * j - Vel.x) * 0.1f, 0, (look.z / i * j - Vel.z) * 0.1f);
            Vel = new Vector3(Vel.x * 0.99f, Vel.y * 0.98f, Vel.z * 0.99f);
            moveVel = Vel; // flying into a wall is judged in FrameMove
            FallFlyTicks++;
            // the elytra loses one durability per second of flight (ElytraItem.elytraFlightTick)
            if ((FallFlyTicks + 1) % 20 == 0) Elytra.Wear();
        }

        // ------------------------------------------------------------------------------------------------ collision

        /// <summary>One frame's share of the current tick's movement, with Minecraft's collision rules.</summary>
        private static void FrameMove(vp_FPController p, CharacterController cc, float dt)
        {
            float ticks = dt * 20f;
            var disp = moveVel * ticks;
            if (Sneaking && OnGround && !Flying)
            {
                var kept = KeepOnEdge(p, disp);
                if (kept.x == 0 && disp.x != 0) moveVel.x = 0;
                if (kept.z == 0 && disp.z != 0) moveVel.z = 0;
                disp = kept;
            }
            var before = p.transform.position;
            var flags = cc.Move(disp);
            var actual = p.transform.position - before;
            bool wasGround = OnGround;
            OnGround = (flags & CollisionFlags.Below) != 0 || (moveVel.y <= 0 && GroundProbe(p, cc));
            bool sides = (flags & CollisionFlags.Sides) != 0;
            if (sides) HorizontalCollision = true;
            if ((flags & CollisionFlags.Above) != 0 && moveVel.y > 0) { Vel.y = 0; moveVel.y = 0; }
            if (sides)
            {
                float speedBefore = new Vector2(moveVel.x, moveVel.z).magnitude;
                // Minecraft drops the velocity on the blocked axis
                if (Mathf.Abs(actual.x) < Mathf.Abs(disp.x) * 0.5f) { Vel.x = 0; moveVel.x = 0; }
                if (Mathf.Abs(actual.z) < Mathf.Abs(disp.z) * 0.5f) { Vel.z = 0; moveVel.z = 0; }
                if (FallFlying)
                {
                    // LivingEntity.travel: flying into a wall hurts by the speed lost
                    float speedAfter = new Vector2(moveVel.x, moveVel.z).magnitude;
                    float dmg = (speedBefore - speedAfter) * 10f - 3f;
                    if (dmg > 0f) { Sounds.Play(dmg > 4 ? "entity.player.big_fall" : "entity.player.small_fall", p.transform.position); Health.Damage(dmg, "flyIntoWall"); }
                }
            }
            // stick to the ground walking down slopes and steps (Minecraft has no slopes, Slime Rancher does)
            if (!OnGround && wasGround && moveVel.y <= 0 && !InWater && !Flying && !FallFlying)
            {
                var pos = p.transform.position;
                var f2 = cc.Move(Vector3.down * 0.62f);
                if ((f2 & CollisionFlags.Below) != 0) OnGround = true;
                else p.transform.position = pos;
                actual = p.transform.position - before;
            }
            if (!OnGround && actual.y < 0) FallDistance -= actual.y;
            if (OnGround)
            {
                if (Vel.y < 0) Vel.y = 0;
                if (moveVel.y < 0) moveVel.y = 0;
                if (FallDistance > 0) Land(p, FallDistance);
                FallDistance = 0;
            }
            tickDisp += actual;
        }

        private static bool GroundProbe(vp_FPController p, CharacterController cc)
        {
            var feet = p.transform.position;
            return Physics.SphereCast(feet + Vector3.up * 0.32f, 0.28f, Vector3.down, out _, 0.06f, SrWorld.SolidMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>PlayerEntity.adjustMovementForSneaking: drop any axis of the move that would leave the player without ground.</summary>
        private static Vector3 KeepOnEdge(vp_FPController p, Vector3 d)
        {
            bool Ground(Vector3 off)
            {
                var feet = p.transform.position + off + Vector3.up * 0.4f;
                return Physics.SphereCast(feet, 0.28f, Vector3.down, out _, 1.0f, SrWorld.SolidMask, QueryTriggerInteraction.Ignore);
            }
            var h = new Vector3(d.x, 0, d.z);
            if (Ground(h)) return d;
            if (Ground(new Vector3(d.x, 0, 0))) { Vel.z = 0; return new Vector3(d.x, d.y, 0); }
            if (Ground(new Vector3(0, 0, d.z))) { Vel.x = 0; return new Vector3(0, d.y, d.z); }
            Vel.x = Vel.z = 0;
            return new Vector3(0, d.y, 0);
        }

        /// <summary>LivingEntity.computeFallDamage: ceil(distance - 3 - jump boost), then Feather Falling / Protection via Health.</summary>
        private static void Land(vp_FPController p, float dist)
        {
            int dmg = Mathf.CeilToInt(dist - 3f - Effects.JumpBoostLevel());
            var under = SrWorld.BlockUnder(p.transform.position);
            if (under != null && (under.Block.Id == "hay_block")) dmg = Mathf.CeilToInt(dmg * 0.2f);
            if (under != null && under.Block.Id == "slime_block" && !Sneaking) { Vel.y = -Vel.y; dmg = 0; }
            if (dmg > 0 && !GameMode.Creative)
            {
                Sounds.Play(dmg > 4 ? "entity.player.big_fall" : "entity.player.small_fall", p.transform.position);
                Health.Damage(dmg, "fall");
                Particles.BlockDust(p.transform.position, under, Mathf.Min(40, 5 + dmg * 5));
            }
            else if (dist > 0.6f) PlayStep(p, 0.3f);
        }

        // ------------------------------------------------------------------------------------------------ sounds

        private static float stepDist, nextStep = 1f;

        private static void Footsteps(vp_FPController p)
        {
            if (!OnGround || Sneaking && !Moving) return;
            stepDist += HorizontalTickSpeed * 0.6f;
            if (stepDist > nextStep)
            {
                nextStep = stepDist + 1f;
                if (InWater && !OnGround) return;
                if (!Sneaking || true) PlayStep(p, 0.15f);
            }
        }

        /// <summary>Step sound of what is underfoot: a Minecraft block's sound group, or the Slime Rancher surface's look-alike.</summary>
        public static void PlayStep(vp_FPController p, float volume)
        {
            var feet = p.transform.position;
            var under = SrWorld.BlockUnder(feet);
            string group = under != null ? under.Block.Sound : SrWorld.SurfaceSound(feet);
            if (InWater) { Sounds.Play("entity.player.swim", feet, 0.05f); return; }
            Sounds.Play(Sounds.BlockEvent(group, "step"), feet, volume, 1f);
        }
    }

    /// <summary>Hands Slime Rancher's player controller to Minecraft's motor when it is ours to drive.</summary>
    [HarmonyPatch]
    internal static class MotorPatches
    {
        [HarmonyPatch(typeof(vp_FPController), "FixedUpdate"), HarmonyPrefix]
        private static bool Fixed(vp_FPController __instance)
        {
            if (!Game.Active) return true;
            Motor.SuspendedByGame = Motor.GameOwnsPlayer(__instance);
            if (Motor.SuspendedByGame) { Motor.Reset(); return true; }
            return false;
        }

        // vp_FPController.Update ends with SmoothMove: that is where the per-frame Minecraft motor runs.
        [HarmonyPatch(typeof(vp_FPController), "SmoothMove"), HarmonyPrefix]
        private static bool Smooth(vp_FPController __instance)
        {
            if (!Game.Active || Motor.SuspendedByGame) return true;
            Perf.Run("Motor", () => Motor.Fixed(__instance));
            return false;
        }

        // Teleporters, respawns and commands: the character controller keeps its old physics position until
        // transforms are synced, and would drag the player back on its next move.
        [HarmonyPatch(typeof(vp_FPController), nameof(vp_FPController.SetPosition)), HarmonyPostfix]
        private static void Teleported(Vector3 position)
        {
            if (!Game.Active) return;
            Motor.Vel = Vector3.zero;
            Motor.FallDistance = 0;
            Motor.Pos = Motor.PrevPos = position + Vector3.up * Motor.EyeHeight;
            Physics.SyncTransforms();
        }

        // Slime Rancher's run, jump, jetpack and vac trigger are Minecraft's business now.
        [HarmonyPatch(typeof(vp_FPInput), "InputRun"), HarmonyPrefix] private static bool NoRun() => !Game.Active;
        [HarmonyPatch(typeof(vp_FPInput), "InputJump"), HarmonyPrefix] private static bool NoJump() => !Game.Active;
        [HarmonyPatch(typeof(vp_FPInput), "InputAttack"), HarmonyPrefix] private static bool NoAttack() => !Game.Active;
    }
}
