using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HarmonyLib;
using InControl;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Runs the Minecraft side while a Slime Rancher world is loaded: sets everything up when Beatrix appears, turns
    /// her into the player (vacpack, jetpack and her HUD bars put away), drives the 20 Hz Minecraft tick, the keys
    /// (E, T, /, F1, F3, F3+F4, F5, A/Q), the HUD and screens, and tears it all down when the world goes away.
    /// </summary>
    public class Game : MonoBehaviour
    {
        public static bool Active;
        public static Game I;
        private GameObject player;
        private static bool dataLoaded;
        private float tickAcc;
        public static KeyCode DropKey = KeyCode.Q;
        private static readonly List<KeyValuePair<float, Action>> later = new List<KeyValuePair<float, Action>>();
        private bool f3Used;
        public static int ScreenClosedFrame = -10;
        private static readonly string[] hiddenHud = { "crossHair", "HealthIcon", "Health Meter", "EnergyIcon", "Energy Meter", "Ammo Slots", "CurrencyIcon", "Currency" };

        [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);

        public static void Later(float seconds, Action a) => later.Add(new KeyValuePair<float, Action>(Time.time + seconds, a));

        /// <summary>Minecraft's files are extracted on the player's own PC by SlimeCraft-Assets.exe, never shipped.</summary>
        public static bool AssetsMissing => !System.IO.File.Exists(System.IO.Path.Combine(McData.Root, "mc.json"));

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
            var cfg = Plugin.Instance.Config.Bind("Controls", "Keyboard", "auto", "auto, azerty or qwerty: decides ZQSD or WASD and whether Minecraft's drop key is A or Q").Value;
            if (cfg == "azerty") DropKey = KeyCode.A;
            if (cfg == "qwerty") DropKey = KeyCode.Q;
            McGui.ConfigScale = Plugin.Instance.Config.Bind("Display", "GuiScale", 0, "Minecraft GUI scale: 0 = Auto (like Minecraft), 1..6 to force").Value;
            DebugChannel.Handlers += TestCommand;
        }

        private void Update()
        {
            var ctx = SRSingleton<SceneContext>.Instance;
            var p = ctx != null ? ctx.Player : null;
            if (p != player)
            {
                if (player != null || p == null) TearDown();
                player = p;
                if (p != null) StartCoroutine(SetupWhenReady(p));
            }
            if (Time.frameCount % 60 == 0) McData.FollowGameLanguage();
            if (!Active || player == null) return;

            try { Frame(); }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }

        private System.Collections.IEnumerator SetupWhenReady(GameObject p)
        {
            // wait for the camera and the save to be there
            while (p != null && (p.GetComponentInChildren<SRFPCamera>() == null || SRSingleton<SceneContext>.Instance.PlayerState == null)) yield return null;
            yield return null;
            if (p == null) yield break;
            if (AssetsMissing) { Plugin.Log.LogError("SlimeCraft: Minecraft assets missing, run SlimeCraft-Assets.exe"); yield break; }
            try { Setup(p); }
            catch (Exception e) { Plugin.Log.LogError("SlimeCraft setup failed: " + e); }
        }

        private void Setup(GameObject p)
        {
            if (!dataLoaded)
            {
                McData.Load();
                McData.FollowGameLanguage();
                Atlas.Build();
                Sounds.Init(this);
                dataLoaded = true;
            }
            Motor.Reset();
            Interaction.Reset();
            var name = SRSingleton<GameContext>.Instance.AutoSaveDirector.SavedGame.GetName();
            Persist.Load(string.IsNullOrEmpty(name) ? "default" : name);
            Sounds.FindMixer(p);
            View.Create(p);
            if (View.I != null) View.I.Perspective = Persist.pendingPerspective;
            GameMode.Set(GameMode.Creative, false);
            Active = true;
            Plugin.Log.LogInfo("SlimeCraft active in " + Persist.Slot);
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
            SrModels.ForgetAll();
            Persist.Slot = null;
        }

        public static bool Gameplay() => Time.timeScale > 0f && SRInput.Instance.GetInputMode() == SRInput.InputMode.DEFAULT;

        private void Frame()
        {
            HideSlimeRancherBits();

            bool gameplay = !Screens.Open && !Chat.Open && Gameplay();
            if (Screens.Open)
            {
                Screens.Update();
                if (!Screens.Open) ScreenClosedFrame = Time.frameCount;
            }
            else if (gameplay)
            {
                if (Input.GetKeyDown(KeyCode.E) && !Motor.SuspendedByGame) Screens.Toggle();
                else if (Input.GetKeyDown(KeyCode.T)) Chat.OpenChat("");
                else if (Input.GetKeyDown(KeyCode.Slash) || Input.GetKeyDown(KeyCode.KeypadDivide)) Chat.OpenChat("/");
            }
            bool menuFree = Time.timeScale > 0 && !Screens.Open && !Chat.Open;
            if (menuFree)
            {
                if (Input.GetKeyDown(KeyCode.F5) && View.I != null) View.I.Perspective = (View.I.Perspective + 1) % 3;
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
                Redstone.Tick();
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

        // ------------------------------------------------------------------------------------------------ Slime Rancher's own look

        private void HideSlimeRancherBits()
        {
            // The vacpack and Beatrix's arms: the Minecraft hand replaces them.
            var vac = player.GetComponentInChildren<WeaponVacuum>(true);
            if (vac != null)
                foreach (var r in vac.GetComponentsInChildren<Renderer>(true))
                    if (!r.forceRenderingOff) r.forceRenderingOff = true;
            // Slime Rancher's bars, vac slots, crosshair and money: the Minecraft HUD shows these now.
            var hud = HudUI.Instance;
            if (hud != null && hud.uiContainer != null)
            {
                var t = hud.uiContainer.transform;
                foreach (var n in hiddenHud)
                {
                    var c = t.Find(n);
                    if (c != null && c.gameObject.activeSelf) c.gameObject.SetActive(false);
                }
            }
        }

        private void OnGUI()
        {
            if (!Active && player != null && AssetsMissing)
            {
                var style = new GUIStyle(GUI.skin.box) { fontSize = Mathf.Max(14, Screen.height / 50), wordWrap = true };
                GUI.Box(new Rect(Screen.width * 0.2f, Screen.height * 0.05f, Screen.width * 0.6f, Screen.height * 0.12f), McData.ModText("missingAssets"), style);
                return;
            }
            if (!Active || player == null) return;
            if (Event.current.type == EventType.KeyDown) Chat.HandleEvent(Event.current);
            GUI.depth = -1000;
            McGui.UpdateScale();
            bool srMenu = SRInput.Instance.GetInputMode() != SRInput.InputMode.DEFAULT;
            if (Screens.Open) { Hud.Draw(); Screens.Draw(); return; }
            if (Time.timeScale == 0f || srMenu) return;
            Hud.Draw();
            Chat.DrawInput();
        }

        // ------------------------------------------------------------------------------------------------ tests

        private static void TestCommand(string verb, string arg)
        {
            var a = arg.Split(' ');
            float F(int i) => float.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture);
            switch (verb)
            {
                case "mc": Commands.Run(arg); break;
                case "slot": Inventory.Selected = int.Parse(a[0]); Hud.ShowItemName(); break;
                case "attack": Interaction.TestAttack(); break;
                case "use": Interaction.TestUse(); break;
                case "inv": Screens.Toggle(); break;
                case "f5": if (View.I != null) View.I.Perspective = int.Parse(a[0]); break;
                case "lang": McData.ForceLang = a[0] == "auto" ? null : a[0]; McData.FollowGameLanguage(); break;
                case "look":
                {
                    var cam = SrWorld.Player.GetComponentInChildren<vp_FPCamera>();
                    cam.Yaw = F(0); cam.Pitch = F(1);
                    break;
                }
                case "tp": SrWorld.Teleport(new Vector3(F(0), F(1), F(2))); break;
                case "walk": Motor.TestMove = new Vector2(F(0), F(1)); Motor.TestUntil = Time.time + F(2); Motor.TestJump = a.Length > 3 && a[3] == "jump"; Motor.TestSprint = a.Length > 3 && a[3] == "sprint"; break;
                case "where":
                {
                    var t = Interaction.Current;
                    Plugin.Log.LogInfo($"feet={SrWorld.Feet:F2} eye={Motor.EyeHeight} ground={Motor.OnGround} water={Motor.InWater}/{Motor.Submerged} vel={Motor.Vel * 20f:F2} target={(t.IsBlock ? "block " + BlockWorld.Get(t.Cell) : t.Mob != null ? "mob " + t.Mob.Type : t.SrObject != null ? "sr " + t.SrObject.name + " terrain=" + t.Terrain : "none")} held={Inventory.Held} mode={(GameMode.Creative ? "creative" : "survival")} hp={Health.Hp} food={Hunger.Food}");
                    break;
                }
                case "nearest":
                {
                    var pl = SrWorld.Feet;
                    Identifiable best = null; float bd = 1e9f;
                    foreach (var id in UnityEngine.Object.FindObjectsOfType<Identifiable>())
                    {
                        if (a[0] != "any" && id.id.ToString() != a[0]) continue;
                        if (id.GetComponent<Vacuumable>() == null) continue;
                        float dd = (id.transform.position - pl).sqrMagnitude;
                        if (dd < bd) { bd = dd; best = id; }
                    }
                    if (best == null) break;
                    var eye = pl + Vector3.up * Motor.EyeHeight;
                    var dir = best.transform.position - eye;
                    if (dir.magnitude > 3f)
                    {
                        var stand = best.transform.position - Vector3.ProjectOnPlane(dir, Vector3.up).normalized * 2.2f + Vector3.up * 0.5f;
                        SrWorld.Teleport(stand);
                        eye = stand + Vector3.up * Motor.EyeHeight;
                        dir = best.transform.position - eye;
                    }
                    var c = SrWorld.Player.GetComponentInChildren<vp_FPCamera>();
                    c.Yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                    c.Pitch = -Mathf.Atan2(dir.y, new Vector2(dir.x, dir.z).magnitude) * Mathf.Rad2Deg;
                    Plugin.Log.LogInfo("nearest " + best.id + " at " + best.transform.position);
                    break;
                }
            }
        }
    }

    /// <summary>The places where Slime Rancher hands over to Minecraft's rules.</summary>
    [HarmonyPatch]
    internal static class GamePatches
    {
        // The vacpack is gone: Steve's hands do the grabbing.
        [HarmonyPatch(typeof(WeaponVacuum), nameof(WeaponVacuum.Update)), HarmonyPrefix]
        private static bool NoVac() => !Game.Active;

        // Escape closing a Minecraft screen or the chat must not also open the pause menu.
        [HarmonyPatch(typeof(PauseMenu), nameof(PauseMenu.Update)), HarmonyPrefix]
        private static bool PauseGuard() => !(Game.Active && (Screens.Open || Chat.Open || Game.ScreenClosedFrame >= Time.frameCount - 1));

        // Our save goes with Slime Rancher's, so the two never disagree about where a slime is.
        [HarmonyPatch(typeof(AutoSaveDirector), "SaveGame"), HarmonyPostfix]
        private static void Saved(AutoSaveDirector __instance)
        {
            try
            {
                var name = __instance.SavedGame.GetName();
                if (Game.Active && !string.IsNullOrEmpty(name) && name == Persist.Slot) Persist.Save();
            }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }

        // ---- right click activates Slime Rancher's buttons, doors and screens (E is Minecraft's inventory)

        private static readonly AccessTools.FieldRef<UIDetector, GameObject> displayingGui = AccessTools.FieldRefAccess<UIDetector, GameObject>("displayingGui");
        private static bool pulseRequested;
        private static int pulseFrame = -1;
        public static bool UiTargeted;

        public static void RequestInteract() { pulseRequested = true; pulseFrame = -1; }

        [HarmonyPatch(typeof(UIDetector), "Update"), HarmonyPostfix]
        private static void DetectorSeen(UIDetector __instance)
        {
            if (Game.Active) UiTargeted = displayingGui(__instance) != null;
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
            if (Game.Active && SRInput.Actions != null && ReferenceEquals(inputKey, SRInput.Actions.interact)) __result = McData.ModText("rightClick");
        }

        // Dying: Minecraft drops the inventory and experience where the player fell (unless keepInventory).
        [HarmonyPatch(typeof(PlayerDeathHandler), "OnDeath"), HarmonyPrefix]
        private static void Died()
        {
            if (!Game.Active) return;
            var at = SrWorld.Feet + Vector3.up;
            Hud.Title("§c" + McData.T("deathScreen.title.hardcore", "You Died!").Replace("Game over!", "You Died!"), McData.Tf("deathScreen.score.value", Xp.Total));
            if (!Commands.KeepInventory && !GameMode.Creative)
            {
                for (int i = 0; i < 36; i++)
                {
                    var s = Inventory.Main[i];
                    if (s == null) continue;
                    ItemEntity.Spawn(s, at, UnityEngine.Random.insideUnitSphere * 0.2f, 2f);
                    Inventory.Main[i] = null;
                }
                for (int i = 0; i < 4; i++) if (Inventory.Armor[i] != null) { ItemEntity.Spawn(Inventory.Armor[i], at, UnityEngine.Random.insideUnitSphere * 0.2f, 2f); Inventory.Armor[i] = null; }
                if (Inventory.Offhand != null) { ItemEntity.Spawn(Inventory.Offhand, at, Vector3.zero, 2f); Inventory.Offhand = null; }
                int xp = Mathf.Min(Xp.Level * 7, 100);
                if (xp > 0) XpOrb.Spawn(at, xp);
                Xp.Level = 0; Xp.Progress = 0; Xp.Total = 0;
            }
            Effects.Clear();
            Hunger.FoodLevel = 20;
            Hunger.Saturation = 5f;
            Motor.Reset();
        }
    }

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
