using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Networking;

namespace SlimeCraft
{
    /// <summary>
    /// Minecraft's own files, read from plugin/assets (extracted from the player's own Minecraft by
    /// tools/extract_assets.py). Textures are point-filtered so they stay as crisp as in Minecraft.
    /// </summary>
    public static class McAssets
    {
        public static string Root => Path.Combine(Plugin.Dir, "assets");

        private static readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
        public static readonly Dictionary<string, string> Lang = new Dictionary<string, string>();
        public static bool SoundsReady;

        public static Texture2D Tex(string rel)
        {
            if (textures.TryGetValue(rel, out var t) && t != null) return t;
            var path = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
            t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (File.Exists(path)) t.LoadImage(File.ReadAllBytes(path));
            else Plugin.Log.LogWarning("missing texture " + rel);
            t.filterMode = FilterMode.Point;
            t.wrapMode = TextureWrapMode.Clamp;
            t.name = "mc:" + rel;
            Object.DontDestroyOnLoad(t);
            textures[rel] = t;
            return t;
        }

        public static string LangCode = "";
        public static bool French => LangCode == "fr_fr";

        /// <summary>Minecraft's own translation file for the language Slime Rancher is set to (English as a fallback).</summary>
        public static string ForceLang; // set by the debug channel to test translations

        public static string GameLangCode()
        {
            if (!string.IsNullOrEmpty(ForceLang)) return ForceLang;
            try
            {
                switch (SRSingleton<GameContext>.Instance.MessageDirector.GetCultureLang())
                {
                    case MessageDirector.Lang.DE: return "de_de";
                    case MessageDirector.Lang.ES: return "es_es";
                    case MessageDirector.Lang.FR: return "fr_fr";
                    case MessageDirector.Lang.RU: return "ru_ru";
                    case MessageDirector.Lang.SV: return "sv_se";
                    case MessageDirector.Lang.ZH: return "zh_cn";
                    case MessageDirector.Lang.JA: return "ja_jp";
                    case MessageDirector.Lang.PT: return "pt_br";
                    case MessageDirector.Lang.KO: return "ko_kr";
                }
            }
            catch { }
            return "en_us";
        }

        /// <summary>Loads the language Slime Rancher uses, if it changed since last time.</summary>
        public static void LoadLang()
        {
            var code = GameLangCode();
            if (code == LangCode) return;
            LangCode = code;
            Lang.Clear();
            var p = Path.Combine(Root, "lang", code + ".txt");
            if (!File.Exists(p)) p = Path.Combine(Root, "lang", "en_us.txt");
            if (!File.Exists(p)) return;
            foreach (var l in File.ReadAllLines(p, System.Text.Encoding.UTF8))
            {
                var i = l.IndexOf('=');
                if (i > 0) Lang[l.Substring(0, i)] = l.Substring(i + 1);
            }
        }

        public static string T(string key, string fallback) => Lang.TryGetValue(key, out var v) ? v : fallback;

        /// <summary>SlimeCraft's own few words, in French or English (English for the other languages).</summary>
        public static string Mod(string key)
        {
            switch (key)
            {
                case "rightClick": return French ? "Clic droit" : "Right click";
                case "slimeStorage": return French ? "Stockage de Slime" : "Slime Storage";
                case "missingAssets":
                    return French
                        ? "SlimeCraft : les textures et sons de Minecraft ne sont pas encore extraits.\nFerme le jeu et lance SlimeCraft-Assets.exe (dans BepInEx\\plugins\\SlimeCraft), il faut Minecraft 1.20.1."
                        : "SlimeCraft: Minecraft's textures and sounds have not been extracted yet.\nClose the game and run SlimeCraft-Assets.exe (in BepInEx\\plugins\\SlimeCraft); Minecraft 1.20.1 is needed.";
            }
            return key;
        }

        /// <summary>Loads every .ogg under assets/sounds in the background; play calls before that are just silent.</summary>
        public static IEnumerator LoadSounds()
        {
            var dir = Path.Combine(Root, "sounds");
            if (!Directory.Exists(dir)) yield break;
            foreach (var f in Directory.GetFiles(dir, "*.ogg", SearchOption.AllDirectories))
            {
                var key = f.Substring(dir.Length + 1).Replace('\\', '/');
                key = key.Substring(0, key.Length - 4);
                using (var req = UnityWebRequestMultimedia.GetAudioClip("file:///" + f.Replace('\\', '/'), AudioType.OGGVORBIS))
                {
                    yield return req.SendWebRequest();
                    if (req.isNetworkError || req.isHttpError) { Plugin.Log.LogWarning("sound " + key + ": " + req.error); continue; }
                    var c = DownloadHandlerAudioClip.GetContent(req);
                    c.name = key;
                    Object.DontDestroyOnLoad(c);
                    clips[key] = c;
                }
            }
            SoundsReady = true;
            Plugin.Log.LogInfo("loaded " + clips.Count + " Minecraft sounds");
        }

        public static AudioClip Clip(string key) => clips.TryGetValue(key, out var c) ? c : null;

        /// <summary>Picks one of key1..keyN, the way a Minecraft sound event picks a random variant.</summary>
        public static AudioClip Variant(string key, int n)
        {
            return Clip(key + Random.Range(1, n + 1));
        }
    }

    /// <summary>
    /// Plays Minecraft sounds through Slime Rancher's own SFX mixer group, so the game's volume slider
    /// treats them like any other sound effect.
    /// </summary>
    public static class McSound
    {
        private static AudioMixerGroup sfx;
        private static readonly List<AudioSource> pool = new List<AudioSource>();
        private static GameObject host;

        private static readonly Dictionary<string, int> counts = new Dictionary<string, int>
        {
            {"dig/cloth",4},{"dig/grass",4},{"dig/gravel",4},{"dig/sand",4},{"dig/snow",4},{"dig/stone",4},{"dig/wood",4},
            {"step/cloth",4},{"step/grass",6},{"step/gravel",4},{"step/sand",5},{"step/snow",4},{"step/stone",6},{"step/wood",6},
            {"random/glass",3},{"random/eat",3},{"damage/hit",3},{"mob/slime/small",5},{"mob/slime/big",4},{"mob/slime/attack",2},
            {"entity/player/attack/strong",6},{"entity/player/attack/weak",4},{"entity/player/attack/knockback",4},
        };

        public static void FindMixer(GameObject player)
        {
            if (sfx != null || player == null) return;
            foreach (var a in player.GetComponentsInChildren<AudioSource>(true))
                if (a.outputAudioMixerGroup != null) { sfx = a.outputAudioMixerGroup; break; }
        }

        private static AudioSource Get()
        {
            if (host == null)
            {
                host = new GameObject("SlimeCraft Sounds");
                Object.DontDestroyOnLoad(host);
                pool.Clear();
            }
            foreach (var s in pool) if (s != null && !s.isPlaying) return s;
            var a = new GameObject("snd").AddComponent<AudioSource>();
            a.transform.SetParent(host.transform);
            a.playOnAwake = false;
            pool.Add(a);
            return a;
        }

        /// <summary>key without the trailing number for multi-variant sounds ("step/wood"), or a full key ("random/pop").</summary>
        public static void Play(string key, Vector3? at, float volume, float pitch)
        {
            AudioClip clip = counts.TryGetValue(key, out var n) ? McAssets.Variant(key, n) : McAssets.Clip(key);
            if (clip == null) return;
            var a = Get();
            a.outputAudioMixerGroup = sfx;
            a.clip = clip;
            a.volume = Mathf.Clamp01(volume);
            a.pitch = pitch;
            if (at.HasValue)
            {
                a.transform.position = at.Value;
                a.spatialBlend = 1f;
                a.rolloffMode = AudioRolloffMode.Linear;
                a.minDistance = 1f;
                a.maxDistance = 16f * Mathf.Max(1f, volume);
            }
            else a.spatialBlend = 0f;
            a.Play();
        }

        public static void Ui(string key, float volume = 0.25f, float pitch = 1f) => Play(key, null, volume, pitch);
    }
}
