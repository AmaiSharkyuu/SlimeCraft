using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Runs the Minecraft side while a Subnautica world is loaded: sets everything up when the player appears,
    /// drives the 20 Hz Minecraft tick, the keys (E, T, /, F1, F3, F3+F4, F5, Q/A), the HUD and screens, and puts
    /// Subnautica's own diver, HUD bars and quick slots away while Amai is out and about.
    /// </summary>
    public class Game : MonoBehaviour
    {
        public static bool Active;
        public static Game I;
        private Player player;
        private static bool dataLoaded;
        private float tickAcc;
        public static KeyCode DropKey = KeyCode.Q;
        private static readonly List<KeyValuePair<float, Action>> later = new List<KeyValuePair<float, Action>>();
        private readonly UiCapture capture = new UiCapture();
        private bool f3Used;

        [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);

        public static void Later(float seconds, Action a) => later.Add(new KeyValuePair<float, Action>(Time.time + seconds, a));

        private void Start()
        {
            I = this;
            try
            {
                int lang = (int)GetKeyboardLayout(0) & 0xFFFF;
                bool azerty = lang == 0x040C || lang == 0x080C;
                DropKey = azerty ? KeyCode.A : KeyCode.Q;
            }
            catch { }
            var cfg = Plugin.Instance.Config.Bind("Controls", "Keyboard", "auto", "auto, azerty or qwerty: decides whether Minecraft's drop key is A or Q").Value;
            if (cfg == "azerty") DropKey = KeyCode.A;
            if (cfg == "qwerty") DropKey = KeyCode.Q;
            McGui.ConfigScale = Plugin.Instance.Config.Bind("Display", "GuiScale", 0, "Minecraft GUI scale: 0 = Auto (like Minecraft), 1..6 to force").Value;
            DebugChannel.Handlers += TestCommand;
        }

        private void Update()
        {
            var p = Player.main;
            if (p != player)
            {
                if (player != null || p == null) TearDown();
                player = p;
                if (p != null) StartCoroutine(SetupWhenReady(p));
            }
            if (!Active || player == null) return;

            try { Frame(); }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }

        private System.Collections.IEnumerator SetupWhenReady(Player p)
        {
            // wait for the world and the save slot to be there
            while (p != null && (MainCamera.camera == null || global::Inventory.main == null || LargeWorldStreamer.main == null || !LargeWorldStreamer.main.IsWorldSettled())) yield return null;
            if (p == null) yield break;
            try { Setup(p); }
            catch (Exception e) { Plugin.Log.LogError("SubnautiCraft setup failed: " + e); }
        }

        private void Setup(Player p)
        {
            if (!dataLoaded)
            {
                McData.Load();
                Atlas.Build();
                Sounds.Init(this);
                dataLoaded = true;
            }
            Motor.Reset();
            Interaction.Reset();
            var slot = SaveLoadManager.main != null ? SaveLoadManager.main.GetCurrentSlot() : "default";
            Persist.Load(string.IsNullOrEmpty(slot) ? "default" : slot);
            View.Create(p);
            if (View.I != null) View.I.Perspective = Persist.pendingPerspective;
            GameMode.Set(GameMode.Creative, false);
            Active = true;
            Plugin.Log.LogInfo("SubnautiCraft active in " + Persist.Slot);
        }

        private void TearDown()
        {
            if (!Active) return;
            Active = false;
            Screens.Close();
            Chat.Close();
            Interaction.Reset();
            ItemEntity.ClearAll();
            Mobs.ClearAll();
            BlockWorld.Clear();
            Particles.Clear();
            Persist.Slot = null;
        }

        private void Frame()
        {
            HideSubnauticaBits();
            SnBridge.Update();

            bool gameplay = !Screens.Open && !Chat.Open && Time.timeScale > 0 && !IngameMenuOpen() && !PdaOpen();
            if ((Chat.Open || Screens.Open) && !InputHandlerStack.main.IsFocused(capture)) InputHandlerStack.main.Push(capture);
            if (Screens.Open) Screens.Update();
            else if (gameplay)
            {
                if (Input.GetKeyDown(KeyCode.E) && !Motor.SuspendedByGame) Screens.Toggle();
                else if (Input.GetKeyDown(KeyCode.T)) Chat.OpenChat("");
                else if (Input.GetKeyDown(KeyCode.Slash) || Input.GetKeyDown(KeyCode.KeypadDivide)) Chat.OpenChat("/");
            }
            if (gameplay)
            {
                if (Input.GetKeyDown(KeyCode.F5) && View.I != null) View.I.Perspective = (View.I.Perspective + 1) % 3;
                if (Input.GetKeyDown(KeyCode.F1) && View.I != null) View.I.HideGui = !View.I.HideGui;
                if (Input.GetKey(KeyCode.F3) && Input.GetKeyDown(KeyCode.F4)) { GameMode.Toggle(); f3Used = true; }
                if (Input.GetKeyUp(KeyCode.F3)) { if (!f3Used) DebugScreen.Shown = !DebugScreen.Shown; f3Used = false; }
            }

            Interaction.Update();

            // 20 Hz Minecraft tick for the world systems
            tickAcc += Time.deltaTime;
            int guard = 0;
            while (tickAcc >= 0.05f && guard++ < 5)
            {
                tickAcc -= 0.05f;
                Health.Tick();
                if (Commands.NaturalRegen) Hunger.Tick();
                Effects.Tick();
                Machines.Tick();
                Machines.Flush();
                Fishing.Tick();
                Riptide.Tick();
                Pickups.Update();
                Mobs.NaturalSpawn();
            }
            if (tickAcc > 0.25f) tickAcc = 0;

            BlockWorld.Update();
            Lights.Update(player.transform.position);
            Atlas.Animate(Time.deltaTime);
            Special.Animate(Time.deltaTime);
            Glint.Animate();
            Particles.Update();

            for (int i = later.Count - 1; i >= 0; i--)
                if (Time.time >= later[i].Key) { var a = later[i].Value; later.RemoveAt(i); try { a(); } catch (Exception e) { Plugin.Log.LogError(e); } }
        }

        public static bool IngameMenuOpen() => IngameMenu.main != null && IngameMenu.main.gameObject.activeInHierarchy && IngameMenu.main.selected;
        public static bool PdaOpen() => Player.main != null && Player.main.GetPDA() != null && Player.main.GetPDA().isInUse;

        // ------------------------------------------------------------------------------------------------ Subnautica's own look

        private static readonly Type[] hudTypes = { typeof(uGUI_HealthBar), typeof(uGUI_FoodBar), typeof(uGUI_WaterBar), typeof(uGUI_OxygenBar), typeof(uGUI_QuickSlots) };
        private float nextHide;

        private void HideSubnauticaBits()
        {
            bool ours = !Motor.SuspendedByGame;
            // the diver's body and arms: Amai replaces them (except in vehicles and chairs, where the game shows its own)
            var view = player.transform.Find("body/player_view");
            if (view != null)
                foreach (var r in view.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (r.forceRenderingOff != ours) r.forceRenderingOff = ours;
            if (Time.time < nextHide) return;
            nextHide = Time.time + 0.5f;
            foreach (var t in hudTypes)
                foreach (var o in FindObjectsOfType(t))
                {
                    var c = (Component)o;
                    var cg = c.GetComponent<CanvasGroup>() ?? c.gameObject.AddComponent<CanvasGroup>();
                    cg.alpha = 0f;
                    cg.blocksRaycasts = false;
                }
            // the dive mask frame: Amai has no helmet
            var mask = FindObjectOfType<PlayerMask>();
            if (mask != null) foreach (var r in mask.GetComponentsInChildren<Renderer>(true)) r.forceRenderingOff = true;
        }

        private void OnGUI()
        {
            if (!Active || player == null) return;
            if (Event.current.type == EventType.KeyDown) Chat.HandleEvent(Event.current);
            GUI.depth = -1000;
            McGui.UpdateScale();
            if (IngameMenuOpen() || WaitScreen.IsWaiting || uGUI.isIntro) return;
            if (PdaOpen()) return;
            if (Screens.Open) { Hud.Draw(); Screens.Draw(); return; }
            if (Time.timeScale == 0f) return;
            Hud.Draw();
            Chat.DrawInput();
        }

        // ------------------------------------------------------------------------------------------------ tests

        /// <summary>Logs the eye and camera position every frame, to check that movement is smooth.</summary>
        private static System.Collections.IEnumerator Trace(int frames)
        {
            var sb = new System.Text.StringBuilder();
            var last = Player.main.transform.position;
            var lastCam = MainCamera.camera.transform.position;
            for (int i = 0; i < frames; i++)
            {
                yield return null;
                var pos = Player.main.transform.position;
                var cam = MainCamera.camera.transform.position;
                sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F4} dt={1:F4} eyeStep={2:F4} camStep={3:F4}", Time.time, Time.deltaTime, (pos - last).magnitude, (cam - lastCam).magnitude));
                last = pos; lastCam = cam;
            }
            DebugChannel.Write(sb.ToString());
            Plugin.Log.LogInfo("trace written");
        }

        private static void TestCommand(string verb, string arg)
        {
            var a = arg.Split(' ');
            float F(int i) => float.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture);
            switch (verb)
            {
                case "mc": Commands.Run(arg); break;
                case "matmode": BlockWorld.SwitchShaders(arg); break;
                case "trace": I.StartCoroutine(Trace(int.Parse(a[0]))); break;
                case "slot": Inventory.Selected = int.Parse(a[0]); Hud.ShowItemName(); break;
                case "attack": Interaction.TestAttack(); break;
                case "inv": Screens.Toggle(); break;
                case "f5": if (View.I != null) View.I.Perspective = int.Parse(a[0]); break;
                case "look":
                {
                    var mcc = MainCameraControl.main;
                    mcc.rotationX = F(0); mcc.rotationY = -F(1);
                    break;
                }
                case "walk": Motor.TestMove = new Vector2(F(0), F(1)); Motor.TestUntil = Time.time + F(2); Motor.TestJump = a.Length > 3 && a[3] == "jump"; Motor.TestSprint = a.Length > 3 && a[3] == "sprint"; break;
                case "where":
                {
                    var t = Interaction.Current;
                    Plugin.Log.LogInfo($"pos={Player.main.transform.position:F2} eye={Motor.EyeHeight} ground={Motor.OnGround} water={Motor.InWater}/{Motor.Submerged} swim={Motor.Swimming} vel={Motor.Vel * 20f:F2} target={(t.IsBlock ? "block " + BlockWorld.Get(t.Cell) : t.Mob != null ? "mob " + t.Mob.Type : t.SnObject != null ? "sn " + t.SnObject.name + " terrain=" + t.Terrain : "none")} held={Inventory.Held} mode={(GameMode.Creative ? "creative" : "survival")} suspended={Motor.SuspendedByGame} hp={Health.Hp} food={Hunger.Food}");
                    break;
                }
                case "invdump":
                {
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < 36; i++) if (Inventory.Main[i] != null) sb.Append(i).Append(':').Append(Inventory.Main[i]).Append("  ");
                    sb.Append(" | SN container: ");
                    foreach (var t in global::Inventory.main.container.GetItemTypes()) sb.Append(t).Append('x').Append(global::Inventory.main.container.GetCount(t)).Append(' ');
                    Plugin.Log.LogInfo(sb.ToString());
                    break;
                }
            }
        }
    }

    /// <summary>Takes Subnautica's input focus while a Minecraft screen or the chat is open (camera and movement stop).</summary>
    public class UiCapture : IInputHandler
    {
        public bool HandleInput() => Screens.Open || Chat.Open;
        public bool HandleLateInput() => Screens.Open || Chat.Open;
        public void OnFocusChanged(InputFocusMode mode)
        {
            if (mode == InputFocusMode.Add || mode == InputFocusMode.Restore) UWE.Utils.lockCursor = false;
        }
    }

    /// <summary>The places where Subnautica hands over to Minecraft's rules.</summary>
    [HarmonyPatch]
    internal static class GamePatches
    {
        // Escape closing a Minecraft screen or the chat must not also open the pause menu; the PDA waits too.
        [HarmonyPatch(typeof(IngameMenu), nameof(IngameMenu.Open)), HarmonyPrefix]
        private static bool NoPause() => !(Game.Active && (Screens.Open || Chat.Open || Screens.JustClosed));

        [HarmonyPatch(typeof(PDA), nameof(PDA.Open)), HarmonyPrefix]
        private static bool NoPda(ref bool __result) { if (Game.Active && (Screens.Open || Chat.Open)) { __result = false; return false; } return true; }

        // Subnautica's lockers open as Minecraft chests, so virtual items can be stored too.
        [HarmonyPatch(typeof(StorageContainer), nameof(StorageContainer.Open), new Type[0]), HarmonyPrefix]
        private static bool Locker(StorageContainer __instance)
        {
            if (!Game.Active || __instance.GetComponentInParent<Planter>() != null) return true;
            Screens.Show(new SnStorageScreen(__instance));
            return false;
        }

        // Our save goes with Subnautica's.
        [HarmonyPatch(typeof(SaveLoadManager), nameof(SaveLoadManager.SaveToTemporaryStorageAsync), typeof(Texture2D)), HarmonyPrefix]
        private static void Saving() { if (Game.Active) Persist.Save(); }

        // Dying: Minecraft drops the inventory and experience where the player fell (unless keepInventory).
        [HarmonyPatch(typeof(Player), nameof(Player.OnKill)), HarmonyPrefix]
        private static void Died(Player __instance)
        {
            if (!Game.Active) return;
            var at = __instance.transform.position;
            Hud.Title("§c" + "Vous êtes mort !", McData.Tf("deathScreen.score.value", Xp.Total));
            if (!Commands.KeepInventory && !GameMode.Creative)
            {
                for (int i = 0; i < 36; i++)
                {
                    var s = Inventory.Main[i];
                    if (s == null) continue;
                    if (s.IsMc || SnBridge.IsVirtual(s.Tech)) { ItemEntity.Spawn(s, at, UnityEngine.Random.insideUnitSphere * 0.2f, 2f); Inventory.Main[i] = null; }
                }
                for (int i = 0; i < 4; i++) if (Inventory.Armor[i] != null) { ItemEntity.Spawn(Inventory.Armor[i], at, UnityEngine.Random.insideUnitSphere * 0.2f, 2f); Inventory.Armor[i] = null; }
                if (Inventory.Offhand != null) { ItemEntity.Spawn(Inventory.Offhand, at, Vector3.zero, 2f); Inventory.Offhand = null; }
                int xp = Mathf.Min(Xp.Level * 7, 100);
                if (xp > 0) XpOrb.Spawn(at, xp);
                Xp.Level = 0; Xp.Progress = 0; Xp.Total = 0;
            }
            Effects.Clear();
            Hunger.Saturation = 5f;
            Motor.Reset();
        }
    }
}
