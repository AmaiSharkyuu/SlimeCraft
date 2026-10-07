using HarmonyLib;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Minecraft's two game modes, switched with F3+F4 like in Minecraft and saved with each Slime Rancher game.
    /// Survival: a small free palette of plain blocks, digging takes time, fall damage, sprinting costs energy.
    /// Creative: every block and every Slime Rancher thing in the inventory, instant breaking, nothing is used up,
    /// no damage, and double-tap jump to fly.
    /// </summary>
    public static class GameMode
    {
        public static bool Creative;

        public static void Set(bool creative, bool announce = true)
        {
            Creative = creative;
            if (!creative) Motor.Flying = false;
            Persist.Dirty = true;
            if (announce)
            {
                var mode = creative ? McAssets.T("gameMode.creative", "Mode Créatif") : McAssets.T("gameMode.survival", "Mode Survie");
                var fmt = McAssets.T("gameMode.changed", "Votre mode de jeu a été changé en %s");
                Hud.Message(fmt.Replace("%s", mode));
            }
        }

        public static void Toggle() => Set(!Creative);
    }

    [HarmonyPatch]
    internal static class CreativePatches
    {
        // Creative players cannot be hurt, whatever hits them.
        [HarmonyPatch(typeof(PlayerDamageable), nameof(PlayerDamageable.Damage)), HarmonyPrefix]
        private static bool NoDamage(ref bool __result)
        {
            if (Game.Active && GameMode.Creative) { __result = false; return false; }
            return true;
        }

        [HarmonyPatch(typeof(PlayerState), nameof(PlayerState.Damage)), HarmonyPrefix]
        private static bool NoStateDamage(ref bool __result)
        {
            if (Game.Active && GameMode.Creative) { __result = false; return false; }
            return true;
        }
    }
}
