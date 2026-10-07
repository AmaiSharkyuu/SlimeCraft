using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// A tiny command file so the game can be driven from outside while it runs: the Claude app sits in
    /// front of the game window, so screenshots and hierarchy dumps are the only way to see what happens.
    /// Write one command per line into %TEMP%\slimecraft_cmd.txt; the file is consumed and the result
    /// lands in the BepInEx log.
    /// </summary>
    public class DebugChannel : MonoBehaviour
    {
        public static readonly string CmdPath = Path.Combine(Path.GetTempPath(), "slimecraft_cmd.txt");
        private float next;

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
                case "shot":
                    ScreenCapture.CaptureScreenshot(arg);
                    break;
                case "newgame":
                    SRSingleton<GameContext>.Instance.AutoSaveDirector.LoadNewGame(arg, Identifiable.Id.PINK_SLIME, PlayerState.GameMode.CLASSIC, () => Plugin.Log.LogError("newgame error"));
                    break;
                case "load":
                {
                    var asd = SRSingleton<GameContext>.Instance.AutoSaveDirector;
                    foreach (var kv in asd.AvailableGamesByGameName())
                    {
                        if (kv.Value.Count == 0) continue;
                        var s = kv.Value[0];
                        Plugin.Log.LogInfo("save: game=" + s.name + " save=" + s.saveName + " display=" + s.displayName);
                        if (s.displayName == arg || s.name == arg) { asd.BeginLoad(s.name, s.saveName, () => Plugin.Log.LogError("load error")); return; }
                    }
                    break;
                }
                case "dump":
                    Dump(arg);
                    break;
                case "layers":
                    for (int i = 0; i < 32; i++) Plugin.Log.LogInfo("layer " + i + " = " + LayerMask.LayerToName(i));
                    break;
                case "timescale":
                    Time.timeScale = float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "shaders":
                {
                    var sb = new StringBuilder();
                    foreach (var sh in Resources.FindObjectsOfTypeAll<Shader>()) sb.AppendLine(sh.name);
                    foreach (var n in new[] { "Standard", "Legacy Shaders/Diffuse", "Unlit/Texture", "Legacy Shaders/Transparent/Cutout/Diffuse", "Sprites/Default", "UI/Default", "Hidden/Internal-Colored", "Unlit/Transparent Cutout" })
                        sb.AppendLine("FIND " + n + " => " + (Shader.Find(n) != null));
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "slimecraft_dump.txt"), sb.ToString());
                    break;
                }
                case "mats":
                {
                    var go = GameObject.Find(arg);
                    var sb = new StringBuilder();
                    if (go != null) foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                        foreach (var m in r.sharedMaterials) if (m != null) sb.AppendLine(r.name + " : " + m.name + " : " + m.shader.name);
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "slimecraft_dump.txt"), sb.ToString());
                    break;
                }
                case "shaderprops":
                {
                    // every material using shaders whose name contains arg, with its properties and values
                    var sb = new StringBuilder();
                    var seen = new System.Collections.Generic.HashSet<string>();
                    foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                    {
                        if (m == null || m.shader == null || !m.shader.name.Contains(arg)) continue;
                        if (!seen.Add(m.shader.name + "|" + m.name)) continue;
                        var sh = m.shader;
                        sb.AppendLine("== " + sh.name + " :: " + m.name + " kw=" + string.Join(",", m.shaderKeywords) + " queue=" + m.renderQueue);
                        for (int i = 0; i < sh.GetPropertyCount(); i++)
                        {
                            var n = sh.GetPropertyName(i);
                            var t = sh.GetPropertyType(i);
                            string v = "";
                            if (t == UnityEngine.Rendering.ShaderPropertyType.Texture) { var tx = m.GetTexture(n); v = tx == null ? "null" : tx.name + " " + tx.width + "x" + tx.height; }
                            else if (t == UnityEngine.Rendering.ShaderPropertyType.Color) v = m.GetColor(n).ToString();
                            else if (t == UnityEngine.Rendering.ShaderPropertyType.Vector) v = m.GetVector(n).ToString();
                            else v = m.GetFloat(n).ToString();
                            sb.AppendLine("   " + n + " (" + t + ") = " + v);
                        }
                    }
                    sb.AppendLine("ambient mode=" + RenderSettings.ambientMode + " sky=" + RenderSettings.ambientSkyColor + " eq=" + RenderSettings.ambientEquatorColor + " ground=" + RenderSettings.ambientGroundColor + " light=" + RenderSettings.ambientLight + " intensity=" + RenderSettings.ambientIntensity + " fog=" + RenderSettings.fog + " " + RenderSettings.fogColor);
                    foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>()) if (l.type == LightType.Directional) sb.AppendLine("dir light " + l.name + " " + l.color + " i=" + l.intensity + " mask=" + l.cullingMask + " rot=" + l.transform.eulerAngles);
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "slimecraft_dump.txt"), sb.ToString());
                    break;
                }
                case "quit":
                    Application.Quit();
                    break;
                default:
                    if (Handlers != null) Handlers(verb, arg);
                    break;
            }
        }

        /// <summary>Other systems hook extra test verbs here.</summary>
        public static event Action<string, string> Handlers;

        private static void Dump(string what)
        {
            var sb = new StringBuilder();
            GameObject go = null;
            if (what == "player" || what == "")
                go = SRSingleton<SceneContext>.Instance?.Player;
            else
                go = GameObject.Find(what);
            if (go == null)
            {
                foreach (var r in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                    sb.AppendLine("root: " + r.name + (r.activeSelf ? "" : " (off)"));
            }
            else Walk(go.transform, 0, sb);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "slimecraft_dump.txt"), sb.ToString());
            Plugin.Log.LogInfo("dumped " + what);
        }

        private static void Walk(Transform t, int depth, StringBuilder sb)
        {
            sb.Append(' ', depth * 2).Append(t.name).Append(t.gameObject.activeSelf ? "" : " (off)")
              .Append(" L").Append(t.gameObject.layer).Append(" pos=").Append(t.localPosition.ToString("F2")).Append(" :");
            foreach (var c in t.GetComponents<Component>())
                if (c != null && !(c is Transform)) sb.Append(' ').Append(c.GetType().Name);
            sb.AppendLine();
            if (depth > 12) return;
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1, sb);
        }
    }
}
