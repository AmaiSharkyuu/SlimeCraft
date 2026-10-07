using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// A command file so the game can be driven from outside while it runs (the Claude app sits in front of the
    /// game window). Write one command per line into %TEMP%\subnauticraft_cmd.txt; the file is consumed and the
    /// result lands in the BepInEx log or in %TEMP%\subnauticraft_dump.txt.
    /// </summary>
    public class DebugChannel : MonoBehaviour
    {
        public static readonly string CmdPath = Path.Combine(Path.GetTempPath(), "subnauticraft_cmd.txt");
        public static readonly string DumpPath = Path.Combine(Path.GetTempPath(), "subnauticraft_dump.txt");
        private float next;

        /// <summary>Other systems hook extra test verbs here.</summary>
        public static event Action<string, string> Handlers;

        private void Update()
        {
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + 0.3f;
            if (!File.Exists(CmdPath)) return;
            string[] lines;
            try { lines = File.ReadAllLines(CmdPath); File.Delete(CmdPath); }
            catch { return; }
            foreach (var l in lines)
            {
                var line = l.Trim();
                if (line.Length == 0) continue;
                try { Run(line); }
                catch (Exception e) { Plugin.Log.LogError("cmd '" + line + "' failed: " + e); }
            }
        }

        private void Run(string line)
        {
            var sp = line.IndexOf(' ');
            var verb = sp < 0 ? line : line.Substring(0, sp);
            var arg = sp < 0 ? "" : line.Substring(sp + 1);
            Plugin.Log.LogInfo("cmd> " + line);
            switch (verb)
            {
                case "shot": ScreenCapture.CaptureScreenshot(arg); break;
                case "newgame":
                {
                    var mode = arg == "creative" ? global::GameMode.Creative : global::GameMode.Survival;
                    var menu = uGUI_MainMenu.main;
                    if (menu != null) Game.I.StartCoroutine(menu.StartNewGame(mode));
                    break;
                }
                case "load":
                    Game.I.StartCoroutine(uGUI_MainMenu.main.LoadGameAsync(arg, "", 0, global::GameMode.Survival));
                    break;
                case "save": Game.I.StartCoroutine(IngameMenu.main.SaveGameAsync()); break;
                case "console": DevConsole.SendConsoleCommand(arg); break;
                case "dump": Dump(arg); break;
                case "timescale": Time.timeScale = float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture); break;
                case "skipintro": { var i = FindObjectOfType<uGUI_SceneIntro>(); if (i != null) i.Stop(true); break; }
                case "shaders":
                {
                    var sb = new StringBuilder();
                    foreach (var sh in Resources.FindObjectsOfTypeAll<Shader>()) sb.AppendLine(sh.name + (sh.isSupported ? "" : " (unsupported)"));
                    foreach (var n in new[] { "Standard", "Legacy Shaders/Diffuse", "Unlit/Texture", "Legacy Shaders/Transparent/Cutout/Diffuse", "Sprites/Default", "UI/Default", "MarmosetUBER", "Hidden/Internal-Colored" })
                        sb.AppendLine("FIND " + n + " => " + (Shader.Find(n) != null));
                    Write(sb.ToString());
                    break;
                }
                case "mats":
                {
                    // materials and shader keywords of every renderer under the named object (or near the player)
                    var sb = new StringBuilder();
                    var go = GameObject.Find(arg);
                    var rs = go != null ? go.GetComponentsInChildren<Renderer>(true) : FindObjectsOfType<Renderer>();
                    int n = 0;
                    foreach (var r in rs)
                    {
                        if (go == null && SrWorld.Player != null && (r.bounds.center - SrWorld.Eye).sqrMagnitude > 900) continue;
                        foreach (var m in r.sharedMaterials) if (m != null) sb.AppendLine(r.name + " L" + r.gameObject.layer + " : " + m.name + " : " + m.shader.name + " kw=" + string.Join(",", m.shaderKeywords) + " q=" + m.renderQueue);
                        if (++n > 300) break;
                    }
                    Write(sb.ToString());
                    break;
                }
                case "shaderprops":
                {
                    var sb = new StringBuilder();
                    var seen = new System.Collections.Generic.HashSet<string>();
                    foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                    {
                        if (m == null || m.shader == null || !m.shader.name.Contains(arg)) continue;
                        if (!seen.Add(m.name)) continue;
                        if (seen.Count > 6) break;
                        var sh = m.shader;
                        sb.AppendLine("== " + sh.name + " :: " + m.name + " kw=" + string.Join(",", m.shaderKeywords) + " queue=" + m.renderQueue);
                        for (int i = 0; i < sh.GetPropertyCount(); i++)
                        {
                            var pn = sh.GetPropertyName(i);
                            var t = sh.GetPropertyType(i);
                            string v;
                            if (t == UnityEngine.Rendering.ShaderPropertyType.Texture) { var tx = m.GetTexture(pn); v = tx == null ? "null" : tx.name; }
                            else if (t == UnityEngine.Rendering.ShaderPropertyType.Color) v = m.GetColor(pn).ToString();
                            else if (t == UnityEngine.Rendering.ShaderPropertyType.Vector) v = m.GetVector(pn).ToString();
                            else v = m.GetFloat(pn).ToString();
                            sb.AppendLine("   " + pn + " (" + t + ") = " + v);
                        }
                    }
                    Write(sb.ToString());
                    break;
                }
                case "layers":
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < 32; i++) { sb.Append("layer " + i + " = " + LayerMask.LayerToName(i) + " collides:"); for (int j = 0; j < 32; j++) if (!Physics.GetIgnoreLayerCollision(i, j)) sb.Append(" " + j); sb.AppendLine(); }
                    Write(sb.ToString());
                    break;
                }
                case "keywords":
                {
                    // every keyword combination used by materials of a shader, with an example material
                    var sb = new StringBuilder();
                    var seen = new System.Collections.Generic.HashSet<string>();
                    foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                    {
                        if (m == null || m.shader == null || m.shader.name != arg) continue;
                        var k = string.Join(",", m.shaderKeywords) + " q=" + m.renderQueue + " cut=" + (m.HasProperty("_EnableCutOff") ? m.GetFloat("_EnableCutOff") : -1) + " mode=" + (m.HasProperty("_Mode") ? m.GetFloat("_Mode") : -1);
                        if (seen.Add(k)) sb.AppendLine(k + "   e.g. " + m.name);
                    }
                    Write(sb.ToString());
                    break;
                }
                case "quit": Application.Quit(); break;
                default: Handlers?.Invoke(verb, arg); break;
            }
        }

        public static void Write(string text) => File.WriteAllText(DumpPath, text);

        private static void Dump(string what)
        {
            var sb = new StringBuilder();
            GameObject go = null;
            if (what == "player" || what == "") go = SrWorld.Player != null ? Player.main.gameObject : null;
            else if (what == "camera") go = Camera.main != null ? Camera.main.transform.root.gameObject : null;
            else go = GameObject.Find(what);
            if (go == null)
                foreach (var r in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                    sb.AppendLine("root: " + r.name + (r.activeSelf ? "" : " (off)"));
            else Walk(go.transform, 0, sb);
            Write(sb.ToString());
            Plugin.Log.LogInfo("dumped " + what);
        }

        private static void Walk(Transform t, int depth, StringBuilder sb)
        {
            sb.Append(' ', depth * 2).Append(t.name).Append(t.gameObject.activeSelf ? "" : " (off)")
              .Append(" L").Append(t.gameObject.layer).Append(" pos=").Append(t.localPosition.ToString("F2")).Append(" :");
            foreach (var c in t.GetComponents<Component>())
                if (c != null && !(c is Transform)) sb.Append(' ').Append(c.GetType().Name);
            sb.AppendLine();
            if (depth > 14) return;
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1, sb);
        }
    }
}
