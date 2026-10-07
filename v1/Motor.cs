using HarmonyLib;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Minecraft's player physics driving Slime Rancher's character controller. Minecraft integrates once per
    /// 20 Hz tick; here every per-tick factor is turned into its exact continuous equivalent so the same speeds,
    /// jump height and air control come out at Unity's physics rate without 20 Hz stutter.
    ///   walk 4.317 m/s, sprint 5.612, sneak 1.295, jump apex 1.2522 blocks, step 0.6, terminal fall 78.4 m/s.
    /// </summary>
    public static class Motor
    {
        public static Vector3 Vel;              // m/s
        public static bool OnGround, Sprinting, Sneaking, InWater, Moving, Flying;
        private static float jumpTapTimer = -1f;
        private static bool jumpWasDown;
        public static float FallDistance;
        public static Vector3 PrevFixedPos, FixedPos;
        public static float HorizontalTickSpeed; // blocks per tick moved horizontally, for view bobbing
        private static float sprintTapTimer = -1f;
        private static bool forwardWasDown;
        private static float eyeHeight = 1.62f, eyeTarget = 1.62f;
        private static float runStaminaPerSecond = 2f;
        private static bool initialised;

        public static float EyeHeight => eyeHeight;

        // Driven by the debug channel so movement can be tested without a keyboard.
        public static Vector2 TestMove; public static float TestUntil; public static bool TestJump, TestSprint, TestSneak;

        private static readonly AccessTools.FieldRef<vp_Controller, bool> grounded = AccessTools.FieldRefAccess<vp_Controller, bool>("m_Grounded");
        private static readonly AccessTools.FieldRef<vp_Controller, bool> groundedNonMountain = AccessTools.FieldRefAccess<vp_Controller, bool>("m_GroundedNonMountain");
        private static readonly AccessTools.FieldRef<vp_Controller, Vector3> velocity = AccessTools.FieldRefAccess<vp_Controller, Vector3>("m_Velocity");
        private static readonly AccessTools.FieldRef<vp_Controller, float> fallSpeed = AccessTools.FieldRefAccess<vp_Controller, float>("m_FallSpeed");
        private static readonly AccessTools.FieldRef<vp_FPController, Vector3> externalForce = AccessTools.FieldRefAccess<vp_FPController, Vector3>("m_ExternalForce");
        private static readonly AccessTools.FieldRef<vp_FPController, Vector3> smoothPos = AccessTools.FieldRefAccess<vp_FPController, Vector3>("m_SmoothPosition");
        private static readonly AccessTools.FieldRef<vp_FPController, Vector3> moveDirection = AccessTools.FieldRefAccess<vp_FPController, Vector3>("m_MoveDirection");

        private const float Tick = 20f;
        private const int Mask = -675375893; // what Slime Rancher's own controller collides with

        public static void Reset()
        {
            Vel = Vector3.zero;
            FallDistance = 0;
            initialised = false;
        }

        private static void Init(vp_FPController c)
        {
            initialised = true;
            var cc = c.CharacterController;
            // Minecraft's player box: 0.6 wide, 1.8 tall (1.5 sneaking), steps up 0.6 blocks.
            cc.radius = 0.3f;
            cc.height = 1.8f;
            cc.center = new Vector3(0, 0.9f, 0);
            cc.stepOffset = 0.6f;
            cc.slopeLimit = 50f;
            var sr = c.GetComponent<StaminaRun>();
            if (sr != null)
            {
                try { runStaminaPerSecond = Traverse.Create(sr).Field("runningStaminaPerSecond").GetValue<float>(); } catch { }
                sr.enabled = false;
            }
            var jet = c.GetComponent<EnergyJetpack>();
            if (jet != null) jet.enabled = false;
            FixedPos = PrevFixedPos = c.transform.position;
        }

        private static float Pow(float f, float ticks) => Mathf.Pow(f, ticks);

        /// <summary>Replaces vp_FPController.FixedUpdate.</summary>
        public static void FixedTick(vp_FPController c)
        {
            if (Time.timeScale == 0f) return;
            if (!initialised) Init(c);
            var cc = c.CharacterController;
            var player = SRSingleton<SceneContext>.Instance.PlayerState;
            var events = (vp_FPPlayerEventHandler)Traverse.Create(c).Property("Player").GetValue();
            float dt = Time.fixedDeltaTime;
            float ticks = dt * Tick;
            bool paused = Screens.Open || events == null;

            PrevFixedPos = c.transform.position;

            // ---- input
            Vector2 move = paused ? Vector2.zero : KeyboardMove(events.InputMoveVector.Get());
            bool jump = !paused && SRInput.Actions.jump.IsPressed;
            bool sneakKey = !paused && (Input.GetKey(KeyCode.LeftShift) || TestSneak);
            bool sprintKey = !paused && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl));
            if (Time.time < TestUntil) { move = TestMove; jump |= TestJump; sprintKey |= TestSprint; }
            bool forward = move.y > 0.8f;

            // ---- creative flight: double-tap jump, like Minecraft
            if (jump && !jumpWasDown && GameMode.Creative)
            {
                if (jumpTapTimer > 0f) { Flying = !Flying; jumpTapTimer = -1f; if (Flying) Vel.y = 0; }
                else jumpTapTimer = 7f / Tick;
            }
            jumpWasDown = jump;
            jumpTapTimer -= dt;
            if (Flying && OnGround && !jump && Vel.y <= 0) Flying = false;
            if (Flying) sneakKey = false; // shift descends instead
            bool flyDown = Flying && !paused && Input.GetKey(KeyCode.LeftShift);

            // ---- sneak (also shrinks the box, but only if there is room to stand back up)
            bool wantSneak = sneakKey;
            if (!wantSneak && Sneaking && Physics.CheckCapsule(c.transform.position + Vector3.up * 0.31f, c.transform.position + Vector3.up * 1.49f, 0.29f, Mask, QueryTriggerInteraction.Ignore))
                wantSneak = true;
            if (wantSneak != Sneaking)
            {
                Sneaking = wantSneak;
                cc.height = Sneaking ? 1.5f : 1.8f;
                cc.center = new Vector3(0, cc.height / 2f, 0);
            }
            eyeTarget = Sneaking ? 1.27f : 1.62f;

            // ---- sprint: Ctrl or a double tap of forward, like Minecraft. Needs energy above 30% (Minecraft's 6 food).
            if (forward && !forwardWasDown)
            {
                if (sprintTapTimer > 0f) sprintKey = true;
                sprintTapTimer = 7f / Tick;
            }
            forwardWasDown = forward;
            sprintTapTimer -= dt;
            bool enoughEnergy = GameMode.Creative || player.GetCurrEnergy() > player.GetMaxEnergy() * 0.3f;
            if (!Sprinting && sprintKey && forward && !Sneaking && enoughEnergy && !InWater) Sprinting = true;
            if (Sprinting && (!forward || Sneaking || (!GameMode.Creative && player.GetCurrEnergy() <= 0) || paused)) Sprinting = false;
            if (Sprinting && !GameMode.Creative && !Flying) player.SpendEnergy(runStaminaPerSecond * dt);

            // ---- water
            InWater = Physics.CheckSphere(c.transform.position + Vector3.up * 0.4f, 0.25f, 1 << 4, QueryTriggerInteraction.Collide);

            // ---- horizontal acceleration (Minecraft's movementInput * 0.98, slowed to 30% when sneaking)
            var yawRot = Quaternion.Euler(0, c.transform.eulerAngles.y, 0);
            var input = new Vector3(move.x, 0, move.y) * 0.98f;
            if (Sneaking) input *= 0.3f;
            if (input.sqrMagnitude > 1f) input.Normalize();
            var wish = yawRot * input;

            float accel, friction;
            if (Flying)
            {
                accel = Sprinting ? 0.1f : 0.05f; friction = 0.91f; // Minecraft's flyingSpeed, doubled when sprinting
            }
            else if (InWater)
            {
                accel = 0.02f; friction = 0.8f;
            }
            else if (OnGround)
            {
                float speed = 0.1f * (Sprinting ? 1.3f : 1f);
                accel = speed;          // 0.1 * 0.216 / 0.6^3: Minecraft's ground acceleration on normal blocks
                friction = 0.6f * 0.91f;
            }
            else
            {
                accel = Sprinting ? 0.026f : 0.02f;
                friction = 0.91f;
            }
            // Per tick Minecraft adds a, moves by v + a, then multiplies by f: the distance covered per tick settles
            // at a / (1 - f). Continuous solution with that terminal speed and the same time constant.
            var hv = new Vector3(Vel.x, 0, Vel.z);
            var target = wish * (accel / (1f - friction)) * Tick; // m/s
            float keep = Pow(friction, ticks);
            hv = target + (hv - target) * keep;

            // ---- vertical
            if (Flying)
            {
                float target2 = ((jump ? 1f : 0f) - (flyDown ? 1f : 0f)) * 0.375f * Tick;
                Vel.y = target2 + (Vel.y - target2) * Pow(0.6f, ticks);
                FallDistance = 0;
            }
            else if (InWater)
            {
                Vel.y = Vel.y * Pow(0.8f, ticks) - 0.02f * Tick * ticks;
                if (jump) Vel.y += 0.04f * Tick * Tick * dt;
                Vel.y = Mathf.Clamp(Vel.y, -6f, 3f);
                FallDistance = 0;
            }
            else
            {
                if (jump && OnGround)
                {
                    Vel.y = 9.26f; // continuous launch speed giving Minecraft's 1.2522 block jump (0.42 b/t discretely)
                    if (Sprinting) hv += yawRot * Vector3.forward * (0.2f * Tick);
                    OnGround = false;
                    PlayStep(c.transform.position, 0.15f);
                }
                // v = (v - 0.08) * 0.98 per tick: terminal -78.4 m/s.
                const float vt = -0.08f * 0.98f / 0.02f * Tick;
                Vel.y = vt + (Vel.y - vt) * Pow(0.98f, ticks);
            }
            Vel.x = hv.x; Vel.z = hv.z;

            // ---- external pushes from Slime Rancher (spring pads, explosions) keep working
            ref var ext = ref externalForce(c);
            Vector3 extMove = ext * (dt * 60f);
            ext /= 1f + c.PhysicsForceDamping;
            if (ext.y > 0.05f) { Vel.y = Mathf.Max(Vel.y, ext.y * 60f); ext.y = 0; }

            // ---- move
            var disp = Vel * dt + extMove;
            if (Sneaking && OnGround) disp = KeepOnEdge(c, disp);
            var before = c.transform.position;
            var flags = cc.Move(disp);
            bool wasGround = OnGround;
            OnGround = (flags & CollisionFlags.Below) != 0;
            // Stick to the ground when walking down slopes and stairs, like Minecraft's step-down.
            if (!OnGround && wasGround && Vel.y <= 0.01f && !InWater)
            {
                if (Physics.SphereCast(c.transform.position + Vector3.up * (cc.radius + 0.05f), cc.radius * 0.95f, Vector3.down, out var hit, 0.65f, Mask, QueryTriggerInteraction.Ignore))
                {
                    var f2 = cc.Move(Vector3.down * (hit.distance + 0.02f));
                    OnGround = (f2 & CollisionFlags.Below) != 0;
                }
            }
            if ((flags & CollisionFlags.Above) != 0 && Vel.y > 0) Vel.y = 0;
            var actual = c.transform.position - before;
            if ((flags & CollisionFlags.Sides) != 0)
            {
                // Minecraft zeroes the blocked axis: keep only the speed we really managed to move with.
                var real = new Vector3(actual.x, 0, actual.z) / dt;
                if (real.magnitude < new Vector3(Vel.x, 0, Vel.z).magnitude) { Vel.x = real.x; Vel.z = real.z; }
                if (Sprinting && real.magnitude < 1f) Sprinting = false;
            }

            // ---- falling and landing
            if (!OnGround && actual.y < 0) FallDistance -= actual.y;
            if (OnGround)
            {
                if (Vel.y < 0) Vel.y = 0;
                if (!wasGround)
                {
                    Land(c, FallDistance);
                }
                FallDistance = 0;
            }

            HorizontalTickSpeed = new Vector3(actual.x, 0, actual.z).magnitude / ticks;
            Moving = HorizontalTickSpeed > 0.01f;
            Footsteps(c, actual);

            // ---- tell Slime Rancher what happened, for everything that reads its controller
            grounded(c) = OnGround;
            groundedNonMountain(c) = OnGround;
            velocity(c) = actual / dt;
            fallSpeed(c) = 0;
            moveDirection(c) = Vector3.zero;
            if (events != null)
            {
                if (Sprinting && !events.Run.Active) events.Run.TryStart();
                if (!Sprinting && events.Run.Active) events.Run.TryStop();
            }
            FixedPos = c.transform.position;
        }

        /// <summary>
        /// Minecraft's movement keys by their printed letters: ZQSD on an AZERTY keyboard, WASD otherwise, plus
        /// arrows. A gamepad stick (Slime Rancher's own input) still works when no key is held.
        /// </summary>
        private static Vector2 KeyboardMove(Vector2 pad)
        {
            bool az = Interaction.AzertyKeyboard;
            float f = (Input.GetKey(az ? KeyCode.Z : KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1 : 0) - (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1 : 0);
            float r = (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1 : 0) - (Input.GetKey(az ? KeyCode.Q : KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1 : 0);
            if (f != 0 || r != 0) return new Vector2(r, f);
            return InputDirector.UsingGamepad() ? pad : Vector2.zero;
        }

        /// <summary>Called every frame instead of vp_FPController.SmoothMove: interpolates between physics steps.</summary>
        public static void Smooth(vp_FPController c)
        {
            if (!initialised) { smoothPos(c) = c.transform.position; return; }
            float t = Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime);
            if ((FixedPos - c.transform.position).sqrMagnitude > 0.0001f) { FixedPos = PrevFixedPos = c.transform.position; } // teleported
            smoothPos(c) = Vector3.Lerp(PrevFixedPos, FixedPos, t);
            eyeHeight = Mathf.MoveTowards(eyeHeight, eyeTarget, Time.deltaTime * 4f);
        }

        /// <summary>Minecraft's sneak edge guard: drop any axis of the move that would leave us without ground.</summary>
        private static Vector3 KeepOnEdge(vp_FPController c, Vector3 d)
        {
            bool Ground(Vector3 off)
            {
                var p = c.transform.position + off + Vector3.up * 0.4f;
                return Physics.SphereCast(p, 0.28f, Vector3.down, out _, 1.0f, Mask, QueryTriggerInteraction.Ignore);
            }
            var h = new Vector3(d.x, 0, d.z);
            if (Ground(h)) return d;
            if (Ground(new Vector3(d.x, 0, 0))) { Vel.z = 0; return new Vector3(d.x, d.y, 0); }
            if (Ground(new Vector3(0, 0, d.z))) { Vel.x = 0; return new Vector3(0, d.y, d.z); }
            Vel.x = Vel.z = 0;
            return new Vector3(0, d.y, 0);
        }

        private static void Land(vp_FPController c, float dist)
        {
            int dmg = Mathf.CeilToInt(dist - 3f);
            if (dmg > 0 && !InWater && !GameMode.Creative)
            {
                // Minecraft hearts are 2 HP out of 20; Slime Rancher has 100 HP, so one Minecraft HP is 5.
                var pd = c.GetComponent<PlayerDamageable>();
                McSound.Play(dmg > 4 ? "damage/fallbig" : "damage/fallsmall", c.transform.position, 1f, 1f);
                if (pd != null) pd.Damage(dmg * 5, null);
            }
            else if (dist > 0.6f) PlayStep(c.transform.position, 0.3f);
        }

        private static float stepDistance;

        private static void Footsteps(vp_FPController c, Vector3 actual)
        {
            if (!OnGround || Sneaking) { if (!OnGround) return; }
            stepDistance += new Vector3(actual.x, 0, actual.z).magnitude * 0.6f;
            // Minecraft plays a step every whole unit of (distance * 0.6).
            if (stepDistance >= 1f)
            {
                stepDistance -= 1f;
                if (!Sneaking) PlayStep(c.transform.position, 0.15f);
            }
        }

        /// <summary>Step sound of whatever is underfoot: a Minecraft block's own group, or a guess for Slime Rancher ground.</summary>
        public static void PlayStep(Vector3 feet, float volume)
        {
            var cell = Blocks.CellAt(feet + Vector3.down * 0.2f);
            if (Blocks.World.TryGetValue(cell, out var p) && !p.Def.Walkable)
            {
                McSound.Play(Blocks.StepSound(p.Def), feet, volume, 1f);
                return;
            }
            if (!Physics.Raycast(feet + Vector3.up * 0.3f, Vector3.down, out var hit, 0.8f, Mask, QueryTriggerInteraction.Ignore)) return;
            string group = "grass";
            var n = (hit.collider.sharedMaterial != null ? hit.collider.sharedMaterial.name : hit.collider.name).ToLowerInvariant();
            if (n.Contains("wood") || n.Contains("plank") || n.Contains("deck") || n.Contains("bridge")) group = "wood";
            else if (n.Contains("rock") || n.Contains("stone") || n.Contains("metal") || n.Contains("ruin") || n.Contains("cliff")) group = "stone";
            else if (n.Contains("sand") || n.Contains("desert")) group = "sand";
            McSound.Play("step/" + group, feet, volume, 1f);
        }
    }
}
