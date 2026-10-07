using HarmonyLib;
using InControl;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Where Slime Rancher hands control over to Minecraft's rules. Every patch only acts while a world is running
    /// with SlimeCraft set up (Game.Active), so menus and loading behave exactly as before.
    /// </summary>
    [HarmonyPatch]
    public static class Patches
    {
        // ---- movement: Minecraft physics instead of vp_FPController's motor

        [HarmonyPatch(typeof(vp_FPController), "FixedUpdate"), HarmonyPrefix]
        private static bool ControllerFixed(vp_FPController __instance)
        {
            if (!Game.Active) return true;
            Motor.FixedTick(__instance);
            return false;
        }

        [HarmonyPatch(typeof(vp_FPController), "SmoothMove"), HarmonyPrefix]
        private static bool ControllerSmooth(vp_FPController __instance)
        {
            if (!Game.Active) return true;
            Motor.Smooth(__instance);
            return false;
        }

        [HarmonyPatch(typeof(vp_FPController), nameof(vp_FPController.SetPosition)), HarmonyPostfix]
        private static void Teleported(vp_FPController __instance, Vector3 position)
        {
            if (!Game.Active) return;
            Motor.Vel = Vector3.zero;
            Motor.FallDistance = 0;
            Motor.PrevFixedPos = Motor.FixedPos = position;
            // The character controller keeps its old physics position until transforms are synced, and would
            // drag the player back on its next move (teleporters, respawn, death warps all come through here).
            Physics.SyncTransforms();
        }

        // Run, jump, jetpack, vac trigger and E-to-interact are all Minecraft's business now.
        [HarmonyPatch(typeof(vp_FPInput), "InputRun"), HarmonyPrefix] private static bool NoRun() => !Game.Active;
        [HarmonyPatch(typeof(vp_FPInput), "InputJump"), HarmonyPrefix] private static bool NoJump() => !Game.Active;
        [HarmonyPatch(typeof(vp_FPInput), "InputAttack"), HarmonyPrefix] private static bool NoAttack() => !Game.Active;

        [HarmonyPatch(typeof(WeaponVacuum), nameof(WeaponVacuum.Update)), HarmonyPrefix]
        private static bool NoVac() => !Game.Active;

        // ---- camera: Minecraft's eye height, bobbing, F5 and hand after Slime Rancher placed the camera

        [HarmonyPatch(typeof(vp_FPCamera), "LateUpdate"), HarmonyPostfix]
        private static void CameraLate(vp_FPCamera __instance)
        {
            if (!Game.Active || View.I == null) return;
            if (!(__instance is SRFPCamera)) return;
            View.I.AfterCamera(__instance.GetComponentInParent<vp_FPController>());
        }

        // ---- right click activates Slime Rancher's buttons, doors and screens

        private static readonly AccessTools.FieldRef<UIDetector, GameObject> displayingGui = AccessTools.FieldRefAccess<UIDetector, GameObject>("displayingGui");
        private static bool pulseRequested;
        private static int pulseFrame = -1;

        public static void RequestInteract() { pulseRequested = true; pulseFrame = -1; }

        [HarmonyPatch(typeof(UIDetector), "Update"), HarmonyPostfix]
        private static void DetectorSeen(UIDetector __instance)
        {
            if (!Game.Active) return;
            Interaction.UiTargeted = displayingGui(__instance) != null;
        }

        [HarmonyPatch(typeof(OneAxisInputControl), nameof(OneAxisInputControl.WasReleased), MethodType.Getter), HarmonyPrefix]
        private static bool InteractReleased(OneAxisInputControl __instance, ref bool __result)
        {
            if (!Game.Active || SRInput.Actions == null || !ReferenceEquals(__instance, SRInput.Actions.interact)) return true;
            if (pulseRequested && pulseFrame < 0) pulseFrame = Time.frameCount;
            if (pulseFrame >= 0 && Time.frameCount > pulseFrame) { pulseRequested = false; pulseFrame = -1; }
            __result = pulseRequested && pulseFrame == Time.frameCount;
            return false;
        }

        [HarmonyPatch(typeof(XlateKeyText), nameof(XlateKeyText.GetKeyString), typeof(MessageBundle), typeof(PlayerAction), typeof(bool), typeof(bool)), HarmonyPostfix]
        private static void InteractKeyName(PlayerAction inputKey, ref string __result)
        {
            if (Game.Active && SRInput.Actions != null && ReferenceEquals(inputKey, SRInput.Actions.interact)) __result = McAssets.Mod("rightClick");
        }

        // ---- Escape closes the Minecraft inventory instead of pausing

        [HarmonyPatch(typeof(PauseMenu), nameof(PauseMenu.Update)), HarmonyPrefix]
        private static bool PauseGuard() => !(Game.Active && (Screens.Open || Game.ScreenClosedFrame >= Time.frameCount - 1));

        // ---- save our inventory and blocks together with Slime Rancher's own save

        [HarmonyPatch(typeof(AutoSaveDirector), "SaveGame"), HarmonyPostfix]
        private static void Saved(AutoSaveDirector __instance)
        {
            try
            {
                var name = __instance.SavedGame.GetName();
                if (!string.IsNullOrEmpty(name) && name == Persist.GameName) Persist.Save();
            }
            catch (System.Exception e) { Plugin.Log.LogError(e); }
        }
    }
}

namespace SlimeCraft
{
    /// <summary>
    /// Slime Rancher's tutorials wait for the vacpack and its own keys; Steve's actions count instead:
    /// walking (ZQSD included) for the movement tip, grabbing for "vac", putting down or throwing for "shoot".
    /// </summary>
    [HarmonyPatch]
    internal static class TutorialPatches
    {
        private static System.Reflection.MethodBase TargetMethod()
            => AccessTools.Method(AccessTools.Inner(typeof(TutorialDirector), "MoveTutorial"), "Update");

        private static void Postfix(object __instance)
        {
            if (!Game.Active || !Motor.Moving) return;
            var t = Traverse.Create(__instance).Field("endTime");
            if (float.IsPositiveInfinity(t.GetValue<float>())) t.SetValue(Time.time + 6f);
        }

        public static void Vac(Identifiable.Id id)
        {
            try { SRSingleton<SceneContext>.Instance.TutorialDirector.OnVac(id); } catch { }
        }

        public static void Shoot(Identifiable.Id id)
        {
            try { SRSingleton<SceneContext>.Instance.TutorialDirector.OnShoot(id); } catch { }
        }
    }
}
