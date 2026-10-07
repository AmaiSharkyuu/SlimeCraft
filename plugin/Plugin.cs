using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Entry point. Everything hangs off one hidden root object that survives scene loads, because Subnautica
    /// swaps between the main menu and the world scene and our Minecraft state (assets, sounds) must survive that.
    /// </summary>
    [BepInPlugin("com.amai.slimecraft", "SlimeCraft", "2.0.0")]
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
            // one class at a time, so a patch that no longer fits the game cannot take the rest down with it
            var harmony = new Harmony("com.amai.slimecraft");
            foreach (var t in typeof(Plugin).Assembly.GetTypes())
            {
                if (t.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) continue;
                try { harmony.CreateClassProcessor(t).Patch(); }
                catch (System.Exception e) { Logger.LogError("patch " + t.Name + " failed: " + e.Message); }
            }

            var root = new GameObject("SlimeCraft");
            root.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(root);
            root.AddComponent<DebugChannel>();
            root.AddComponent<Game>();
            Log.LogInfo("SlimeCraft loaded from " + Dir);
        }
    }
}
