using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Entry point. Everything else hangs off one hidden root object that survives scene loads,
    /// because Slime Rancher swaps scenes between the main menu and the world and we must keep
    /// our Minecraft state (assets, sounds) across that.
    /// </summary>
    [BepInPlugin("com.amai.slimecraft", "SlimeCraft", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;
        public static Plugin Instance;
        public static string Dir;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            Dir = System.IO.Path.GetDirectoryName(Info.Location);
            new Harmony("com.amai.slimecraft").PatchAll();

            var root = new GameObject("SlimeCraft");
            root.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(root);
            root.AddComponent<DebugChannel>();
            root.AddComponent<Game>();
            Log.LogInfo("SlimeCraft loaded from " + Dir);
        }
    }
}
