using HarmonyLib;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Minecraft's player movement, run as real 20 Hz ticks (LivingEntity.travel, PlayerEntity.travel,
    /// ClientPlayerEntity.tickMovement) on Subnautica's own character controller. All numbers are Minecraft's,
    /// in blocks (= metres) per tick. The camera is interpolated between ticks by View, like Minecraft does.
    ///
    ///  * ground: speed 0.1 (sprint x1.3, sneak x0.3), slipperiness of the Minecraft block underfoot, jump 0.42
    ///  * water (Subnautica's ocean, moon pools, aquariums): 0.02 accel, drag 0.8 (0.9 sprinting), sinking by
    ///    gravity/16, space swims up 0.04; sprinting underwater is Minecraft's swimming pose that follows the look
    ///  * Depth Strider = Subnautica fins, Dolphin's Grace = ultra glide fins or the effect, seaglide = a strong push
    ///  * creative flight (double jump), elytra gliding (jump in the air) and firework boosts
    ///  * fall damage, fly-into-wall damage, exhaustion for the hunger bar
    /// The game takes over in vehicles, chairs, cinematics and inside the moving Cyclops.
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

        /// <summary>When the game owns the player (vehicles, chairs, cinematics, Cyclops) our motor stands aside.</summary>
        public static bool GameOwnsPlayer(Player p)
        {
            if (p == null) return true;
            if (p.GetMode() != Player.Mode.Normal || p.cinematicModeActive || p.GetVehicle() != null) return true;
            if (p.currentSub != null && p.currentSub.isCyclops) return true;
            if (!p.liveMixin.IsAlive()) return true;
            return false;
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
            cc.center = new Vector3(0, -eye + height / 2f, 0);
            cc.stepOffset = Mathf.Min(0.6f, height - 0.01f);
            cc.slopeLimit = 50f;
            cc.skinWidth = 0.03f;
        }

        private static bool Fits(Vector3 eyePos, float height, float eye)
        {
            var feet = eyePos - Vector3.up * eye;
            return !Physics.CheckCapsule(feet + Vector3.up * 0.31f, feet + Vector3.up * (height - 0.31f), 0.29f, SnWorld.SolidMask, QueryTriggerInteraction.Ignore);
        }

        // ------------------------------------------------------------------------------------------------ fixed step

        /// <summary>Replaces PlayerController.Update while Minecraft owns the player (every frame).</summary>
        public static void Fixed(PlayerController pc)
        {
            var p = pc.player;
            var gm = (GroundMotor)pc.groundController;
            var um = pc.underWaterController;
            var cc = gm.controller;
            if (!initialised)
            {
                initialised = true;
                um.SetEnabled(false);
                gm.SetEnabled(true);
                pc.activeController = gm;
                Pos = PrevPos = p.transform.position;
            }
            if (um.enabled) um.SetEnabled(false);
            if (!gm.enabled || !cc.enabled) { gm.SetEnabled(true); pc.activeController = gm; }
            if (!pc.useRigidbody.isKinematic) UWE.Utils.SetIsKinematicAndUpdateInterpolation(pc.useRigidbody, true, false);

            float dt = Time.deltaTime;
            if (dt <= 0f || Time.timeScale == 0f) return;
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
                Tick(p, pc, cc);
                HorizontalCollision = false;
            }
            if (guard >= 5) acc = 0;
            pc.velocity = moveVel * 20f;
            gm.grounded = OnGround;
        }

        private static Vector3 moveVel, tickDisp;

        /// <summary>How far we are between the last 20 Hz tick and the next, counting the time since the last physics step.</summary>
        public static float Partial => Mathf.Clamp01(acc / 0.05f);

        private static void Tick(Player p, PlayerController pc, CharacterController cc)
        {
            PrevPos = p.transform.position;
            PrevEyeHeight = EyeHeight;
            PrevSwimAmount = SwimAmount;
            bool paused = Screens.Open || Chat.Open || (p.GetPDA() != null && p.GetPDA().isInUse) || !pc.inputEnabled;

            // ---- input (Subnautica's bindings for moving so Ezio's ZQSD setup applies; Minecraft keys for the rest)
            Vector2 move = Vector2.zero;
            bool jump = false, sneakKey = false, sprintKey = false;
            if (!paused)
            {
                var md = GameInput.GetMoveDirection();
                move = new Vector2(Mathf.Clamp(md.x, -1, 1), Mathf.Clamp(md.z, -1, 1));
                jump = GameInput.GetButtonHeld(GameInput.Button.Jump) || Input.GetKey(KeyCode.Space);
                sneakKey = Input.GetKey(KeyCode.LeftShift) || TestSneak;
                sprintKey = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            }
            if (Time.time < TestUntil) { move = TestMove; jump |= TestJump; sprintKey |= TestSprint; }
            bool forward = move.y > 0.8f;

            // ---- water (Minecraft's isTouchingWater / isSubmergedInWater against Subnautica's water surface)
            float level = SnWorld.WaterLevelAt(p);
            var eyePos = p.transform.position;
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
                // Keep the feet where they are when the box changes: move the eye (transform) instead.
                float newEye = eyeH;
                var feet = eyePos - Vector3.up * EyeHeight;
                if (Fits(feet + Vector3.up * newEye, boxH, newEye) || boxH < cc.height)
                {
                    p.transform.position = feet + Vector3.up * newEye;
                    PrevPos += Vector3.up * (newEye - EyeHeight);
                    EyeHeight = newEye;
                    ApplyBox(cc, boxH, eyeH);
                }
            }
            else ApplyBox(cc, boxH, eyeH);

            // ---- movement input, Minecraft style (sideways, forward) * 0.98, sneaking x0.3
            var input = new Vector3(move.x, 0, move.y) * 0.98f;
            if (Sneaking) input *= 0.3f + 0.15f * Enchants.Level(Inventory.ArmorIn("legs"), "swift_sneak");
            LastInput = input;
            var cam = MainCamera.camera.transform;
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
                    bool fluidAbove = SnWorld.WaterLevelAt(p) > eyePos.y + 1f;
                    if (d <= 0f || jump || fluidAbove) Vel.y += (d - Vel.y) * e;
                }
                float f = Sprinting ? 0.9f : 0.8f;
                float g = 0.02f;
                float h = Mathf.Min(3f, Enchants.DepthStrider());
                if (!OnGround) h *= 0.5f;
                if (h > 0f) { f += (0.546f - f) * h / 3f; g += (speedAttr - g) * h / 3f; }
                if (Effects.Has("dolphins_grace") || SnWorld.HasUltraGlideFins()) f = 0.96f;
                g *= SnWorld.SeaglideBoost(move.y);
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
                if (HorizontalCollision && Fits(p.transform.position + new Vector3(Vel.x, Vel.y + 0.6f, Vel.z), boxH, eyeH)) Vel.y = 0.3f;
                FallDistance = 0;
            }
            else
            {
                // ---- ground / air / creative flight (LivingEntity.travel last branch + PlayerEntity flying)
                var under = SnWorld.BlockUnder(p.transform.position - Vector3.up * EyeHeight);
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

        private static bool Climbing(Player p)
        {
            var feet = p.transform.position - Vector3.up * EyeHeight;
            var s = BlockWorld.Get(BlockWorld.CellAt(feet + Vector3.up * 0.1f));
            return s != null && s.Block.Tags.Contains("climbable");
        }

        /// <summary>LivingEntity.travel, fall-flying branch, unchanged.</summary>
        private static void TravelGlide(Vector3 look, CharacterController cc, Player p)
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
        private static void FrameMove(Player p, CharacterController cc, float dt)
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
            // stick to the ground walking down slopes and steps (Minecraft has no slopes, Subnautica does)
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

        private static bool GroundProbe(Player p, CharacterController cc)
        {
            var feet = p.transform.position + cc.center - Vector3.up * (cc.height / 2f);
            return Physics.SphereCast(feet + Vector3.up * 0.32f, 0.28f, Vector3.down, out _, 0.06f, SnWorld.SolidMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>PlayerEntity.adjustMovementForSneaking: drop any axis of the move that would leave the player without ground.</summary>
        private static Vector3 KeepOnEdge(Player p, Vector3 d)
        {
            bool Ground(Vector3 off)
            {
                var feet = p.transform.position - Vector3.up * EyeHeight + off + Vector3.up * 0.4f;
                return Physics.SphereCast(feet, 0.28f, Vector3.down, out _, 1.0f, SnWorld.SolidMask, QueryTriggerInteraction.Ignore);
            }
            var h = new Vector3(d.x, 0, d.z);
            if (Ground(h)) return d;
            if (Ground(new Vector3(d.x, 0, 0))) { Vel.z = 0; return new Vector3(d.x, d.y, 0); }
            if (Ground(new Vector3(0, 0, d.z))) { Vel.x = 0; return new Vector3(0, d.y, d.z); }
            Vel.x = Vel.z = 0;
            return new Vector3(0, d.y, 0);
        }

        /// <summary>LivingEntity.computeFallDamage: ceil(distance - 3 - jump boost), then Feather Falling / Protection via Health.</summary>
        private static void Land(Player p, float dist)
        {
            int dmg = Mathf.CeilToInt(dist - 3f - Effects.JumpBoostLevel());
            var under = SnWorld.BlockUnder(p.transform.position - Vector3.up * EyeHeight);
            if (under != null && (under.Block.Id == "hay_block")) dmg = Mathf.CeilToInt(dmg * 0.2f);
            if (under != null && under.Block.Id == "slime_block" && !Sneaking) { Vel.y = -Vel.y; dmg = 0; }
            if (dmg > 0 && !GameMode.Creative)
            {
                Sounds.Play(dmg > 4 ? "entity.player.big_fall" : "entity.player.small_fall", p.transform.position);
                Health.Damage(dmg, "fall");
                Particles.BlockDust(p.transform.position - Vector3.up * EyeHeight, under, Mathf.Min(40, 5 + dmg * 5));
            }
            else if (dist > 0.6f) PlayStep(p, 0.3f);
        }

        // ------------------------------------------------------------------------------------------------ sounds

        private static float stepDist, nextStep = 1f;

        private static void Footsteps(Player p)
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

        /// <summary>Step sound of what is underfoot: a Minecraft block's sound group, or the Subnautica surface's look-alike.</summary>
        public static void PlayStep(Player p, float volume)
        {
            var feet = p.transform.position - Vector3.up * EyeHeight;
            var under = SnWorld.BlockUnder(feet);
            string group = under != null ? under.Block.Sound : SnWorld.SurfaceSound(feet);
            if (InWater) { Sounds.Play("entity.player.swim", feet, 0.05f); return; }
            Sounds.Play(Sounds.BlockEvent(group, "step"), feet, volume, 1f);
        }
    }

    /// <summary>Hands the player to Minecraft's motor when it is ours to drive.</summary>
    [HarmonyPatch]
    internal static class MotorPatches
    {
        [HarmonyPatch(typeof(PlayerController), "FixedUpdate"), HarmonyPrefix]
        private static bool Fixed(PlayerController __instance)
        {
            if (!Game.Active) return true;
            Motor.SuspendedByGame = Motor.GameOwnsPlayer(__instance.player);
            if (Motor.SuspendedByGame) { Motor.Reset(); return true; }
            return false;
        }

        [HarmonyPatch(typeof(PlayerController), "Update"), HarmonyPrefix]
        private static bool PcUpdate(PlayerController __instance)
        {
            if (!Game.Active) return true;
            Motor.SuspendedByGame = Motor.GameOwnsPlayer(__instance.player);
            if (Motor.SuspendedByGame) return true;
            Motor.Fixed(__instance);
            return false;
        }

        // Subnautica's own handling of the controller size (swim vs stand) would fight our pose boxes.
        [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.ForceControllerSize)), HarmonyPrefix]
        private static bool NoForce(ref Vector3 __result) { if (!Game.Active || Motor.SuspendedByGame) return true; __result = Vector3.zero; return false; }

        [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.SetMotorMode)), HarmonyPostfix]
        private static void AfterMotorMode(PlayerController __instance)
        {
            if (!Game.Active || Motor.SuspendedByGame) return;
            __instance.underWaterController.SetEnabled(false);
            __instance.groundController.SetEnabled(true);
            __instance.activeController = __instance.groundController;
        }
    }
}
