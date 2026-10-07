using System.Runtime.InteropServices;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Watches for Slime Rancher's world to come up, turns Beatrix into Steve (vacpack and jetpack off, Minecraft
    /// body, hand, HUD and inventory on), and tears it all down again when the world goes away.
    /// </summary>
    public class Game : MonoBehaviour
    {
        public static bool Active;
        public static int ScreenClosedFrame = -10;
        private GameObject player;
        private bool defsLoaded;
        private static readonly string[] hiddenHud = { "crossHair", "HealthIcon", "Health Meter", "EnergyIcon", "Energy Meter", "Ammo Slots", "CurrencyIcon", "Currency" };

        [DllImport("user32.dll")] private static extern System.IntPtr GetKeyboardLayout(uint thread);

        private void Start()
        {
            McAssets.LoadLang();
            StartCoroutine(McAssets.LoadSounds());
            try
            {
                int lang = (int)GetKeyboardLayout(0) & 0xFFFF;
                Interaction.AzertyKeyboard = lang == 0x040C || lang == 0x080C; // French (France), French (Belgium)
            }
            catch { }
            var cfgLayout = Plugin.Instance.Config.Bind("Controls", "Keyboard", "auto", "auto, azerty or qwerty: decides whether Minecraft's drop key is A or Q").Value;
            if (cfgLayout == "azerty") Interaction.AzertyKeyboard = true;
            if (cfgLayout == "qwerty") Interaction.AzertyKeyboard = false;
            McGui.ConfigScale = Plugin.Instance.Config.Bind("Display", "GuiScale", 0, "Minecraft GUI scale: 0 = Auto (like Minecraft), 1..6 to force").Value;
            DebugChannel.Handlers += TestCommand;
        }

        private void Update()
        {
            var ctx = SRSingleton<SceneContext>.Instance;
            var p = ctx != null ? ctx.Player : null;
            if (p != player)
            {
                if (player == null || p == null) TearDown();
                if (p != null) Setup(p);
                player = p;
            }
            if (!Active || player == null) return;

            HideSlimeRancherBits();

            if (Screens.Open)
            {
                Screens.Update();
                if (!Screens.Open) ScreenClosedFrame = Time.frameCount;
            }
            else if (Input.GetKeyDown(KeyCode.E) && Gameplay())
                Screens.Show();

            if (Input.GetKeyDown(KeyCode.F5) && Gameplay() && View.I != null) View.I.Perspective = (View.I.Perspective + 1) % 3;
            // F3 + F4: Minecraft's game mode switcher, here straight between survival and creative.
            if (Input.GetKey(KeyCode.F3) && Input.GetKeyDown(KeyCode.F4) && Gameplay()) GameMode.Toggle();
            Blocks.AnimateLids(Time.deltaTime);

            var cam = player.GetComponentInChildren<SRFPCamera>();
            Interaction.Update(cam != null ? cam.GetComponent<Camera>() : null);
        }

        private static bool Gameplay() => Time.timeScale > 0f && SRInput.Instance.GetInputMode() == SRInput.InputMode.DEFAULT;

        /// <summary>Minecraft's files are extracted on the player's own PC by SlimeCraft-Assets.exe, never shipped.</summary>
        public static bool AssetsMissing => !System.IO.File.Exists(System.IO.Path.Combine(McAssets.Root, "blocks.txt"));

        private void Setup(GameObject p)
        {
            if (AssetsMissing) { Plugin.Log.LogError("SlimeCraft: Minecraft assets missing, run SlimeCraft-Assets.exe"); return; }
            if (!defsLoaded) { Blocks.LoadDefs(); defsLoaded = true; }
            Motor.Reset();
            Interaction.Reset();
            var asd = SRSingleton<GameContext>.Instance.AutoSaveDirector;
            Persist.Load(asd.SavedGame.GetName() ?? "default");
            McSound.FindMixer(p);
            p.AddComponent<View>().Setup(p);
            Active = true;
            Plugin.Log.LogInfo("SlimeCraft active in " + Persist.GameName);
        }

        private void TearDown()
        {
            if (!Active) return;
            Active = false;
            Screens.Destroy();
            Interaction.Reset();
            ItemEntity.ClearAll();
            Blocks.Clear();
            ActorVisuals.ForgetAll();
            Persist.GameName = null;
        }

        private void HideSlimeRancherBits()
        {
            // The vacpack and Beatrix's arms: Steve's hand replaces them.
            var vac = player.GetComponentInChildren<WeaponVacuum>(true);
            if (vac != null)
                foreach (var r in vac.GetComponentsInChildren<Renderer>(true)) r.forceRenderingOff = true;
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
                GUI.Box(new Rect(Screen.width * 0.2f, Screen.height * 0.05f, Screen.width * 0.6f, Screen.height * 0.12f),
                    "SlimeCraft : les textures et sons de Minecraft ne sont pas encore extraits.\n" +
                    "Ferme le jeu et lance SlimeCraft-Assets.exe (dans BepInEx\\plugins\\SlimeCraft), il faut Minecraft 1.20.1.", style);
                return;
            }
            if (!Active || player == null) return;
            GUI.depth = -1000;
            McGui.UpdateScale();
            bool srMenu = SRInput.Instance.GetInputMode() != SRInput.InputMode.DEFAULT;
            if (Screens.Open) { Hud.Draw(); Screens.Draw(); return; }
            if (Time.timeScale == 0f || srMenu) return;
            Hud.Draw();
        }

        private void OnApplicationQuit()
        {
            // Slime Rancher saves on quit through AutoSaveDirector, which also writes our file.
        }

        // ------------------------------------------------------------------------------------------------ tests

        private static void TestCommand(string verb, string arg)
        {
            var a = arg.Split(' ');
            float F(int i) => float.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture);
            switch (verb)
            {
                case "give":
                    if (Blocks.ById.TryGetValue(a[0], out var d)) Inventory.Add(new ItemStack(d, a.Length > 1 ? int.Parse(a[1]) : 64));
                    else Inventory.Add(new ItemStack((Identifiable.Id)System.Enum.Parse(typeof(Identifiable.Id), a[0]), a.Length > 1 ? int.Parse(a[1]) : 1));
                    break;
                case "blockshader":
                    Blocks.UseLegacy = a[0] == "legacy";
                    if (a.Length > 1) Blocks.DepthMode = a[1];
                    Blocks.ResetMaterials();
                    break;
                case "clear": for (int i = 9; i < 36; i++) Inventory.Slots[i] = null; break;
                case "mode": GameMode.Set(a[0] == "creative"); break;
                case "slot": Inventory.Selected = int.Parse(a[0]); Hud.ShowItemName(); break;
                case "use": Interaction.TestUse(); break;
                case "attack": Interaction.TestAttack(); break;
                case "dig": Interaction.TestDig(int.Parse(a[0])); break;
                case "drop": Interaction.Drop(false); break;
                case "inv": Screens.Toggle(); break;
                case "tab": Screens.TestTab(int.Parse(a[0])); break;
                case "f5": if (View.I != null) View.I.Perspective = int.Parse(a[0]); break;
                case "look":
                {
                    var cam = SRSingleton<SceneContext>.Instance.Player.GetComponentInChildren<vp_FPCamera>();
                    cam.Yaw = F(0); cam.Pitch = F(1);
                    break;
                }
                case "tp":
                    SRSingleton<SceneContext>.Instance.Player.GetComponent<vp_FPController>().SetPosition(new Vector3(F(0), F(1), F(2)));
                    break;
                case "walk": Motor.TestMove = new Vector2(F(0), F(1)); Motor.TestUntil = Time.time + F(2); Motor.TestJump = a.Length > 3 && a[3] == "jump"; Motor.TestSprint = a.Length > 3 && a[3] == "sprint"; break;
                case "sneak": Motor.TestSneak = a[0] == "1"; break;
                case "where":
                {
                    var pl = SRSingleton<SceneContext>.Instance.Player;
                    var cam = pl.GetComponentInChildren<vp_FPCamera>();
                    Plugin.Log.LogInfo($"pos={pl.transform.position:F2} yaw={cam.Yaw:F1} pitch={cam.Pitch:F1} ground={Motor.OnGround} vel={Motor.Vel:F2} target={(Interaction.Current.Actor != null ? Interaction.Current.Actor.id.ToString() : Interaction.Current.Block != null ? "block" : Interaction.Current.Any ? Interaction.Current.Hit.collider.name : "none")} held={Inventory.Held?.Name}");
                    break;
                }
                case "nearest":
                {
                    var pl = SRSingleton<SceneContext>.Instance.Player.transform.position;
                    Identifiable best = null; float bd = 1e9f;
                    foreach (var id in Object.FindObjectsOfType<Identifiable>())
                    {
                        if (a[0] != "any" && id.id.ToString() != a[0]) continue;
                        if (id.GetComponent<Vacuumable>() == null) continue;
                        float dd = (id.transform.position - pl).sqrMagnitude;
                        if (dd < bd) { bd = dd; best = id; }
                    }
                    if (best != null)
                    {
                        var cam = SRSingleton<SceneContext>.Instance.Player.GetComponentInChildren<vp_FPCamera>();
                        var eye = pl + Vector3.up * Motor.EyeHeight;
                        var dir = best.transform.position - eye;
                        if (dir.magnitude > 3f)
                        {
                            var stand = best.transform.position - Vector3.ProjectOnPlane(dir, Vector3.up).normalized * 2.2f + Vector3.up * 1.5f;
                            SRSingleton<SceneContext>.Instance.Player.GetComponent<vp_FPController>().SetPosition(stand);
                            eye = stand + Vector3.up * Motor.EyeHeight;
                            dir = best.transform.position - eye;
                        }
                        cam.Yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                        cam.Pitch = -Mathf.Atan2(dir.y, new Vector2(dir.x, dir.z).magnitude) * Mathf.Rad2Deg;
                        Plugin.Log.LogInfo("nearest " + best.id + " at " + best.transform.position);
                    }
                    break;
                }
            }
        }
    }
}
