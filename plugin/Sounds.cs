using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace SlimeCraft
{
    /// <summary>
    /// Minecraft's sound events (sounds.json): an event like "block.stone.break" picks one of its files by weight,
    /// with that file's own volume and pitch, exactly like Minecraft's SoundManager. Files are loaded the first
    /// time they are needed and kept compressed in memory. Unity audio is re-enabled in Subnautica by Tobey's
    /// UnityAudio patcher, which Ezio's mod setup already has.
    /// </summary>
    public static class Sounds
    {
        private static readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
        private static readonly HashSet<string> loading = new HashSet<string>();
        private static readonly List<AudioSource> pool = new List<AudioSource>();
        private static GameObject host;
        private static MonoBehaviour runner;

        public static void Init(MonoBehaviour r)
        {
            runner = r;
            // the ones played constantly, so the first footstep is not silent
            foreach (var ev in new[] { "block.stone.step", "block.grass.step", "block.sand.step", "block.wood.step", "block.gravel.step",
                                       "entity.player.attack.strong", "entity.player.attack.weak", "entity.player.hurt", "entity.item.pickup",
                                       "block.stone.break", "block.stone.hit", "block.sand.break", "block.stone.place", "entity.generic.eat",
                                       "entity.player.swim", "entity.player.splash", "item.armor.equip_generic", "ui.button.click" })
                Preload(ev);
        }

        private static void Preload(string ev)
        {
            if (!McData.SoundEvents.TryGetValue(ev, out var list)) return;
            foreach (var s in list) if (s["file"] != null) Load((string)s["file"]);
        }

        private static void Load(string file)
        {
            if (clips.ContainsKey(file) || loading.Contains(file) || runner == null) return;
            loading.Add(file);
            runner.StartCoroutine(LoadRoutine(file));
        }

        private static IEnumerator LoadRoutine(string file)
        {
            var path = Path.Combine(McData.Root, "sounds", file.Replace('/', Path.DirectorySeparatorChar) + ".ogg");
            if (!File.Exists(path)) { clips[file] = null; yield break; }
            using (var req = UnityWebRequestMultimedia.GetAudioClip("file:///" + path.Replace('\\', '/'), AudioType.OGGVORBIS))
            {
                ((DownloadHandlerAudioClip)req.downloadHandler).compressed = true;
                yield return req.SendWebRequest();
                AudioClip c = null;
                if (!req.isNetworkError && !req.isHttpError) c = DownloadHandlerAudioClip.GetContent(req);
                if (c != null) { c.name = file; Object.DontDestroyOnLoad(c); }
                clips[file] = c;
                loading.Remove(file);
            }
        }

        private static AudioSource Source()
        {
            if (host == null)
            {
                host = new GameObject("SubnautiCraft Sounds");
                Object.DontDestroyOnLoad(host);
                pool.Clear();
            }
            foreach (var s in pool) if (s != null && !s.isPlaying) return s;
            if (pool.Count > 48) return pool[Random.Range(0, pool.Count)];
            var a = new GameObject("snd").AddComponent<AudioSource>();
            a.transform.SetParent(host.transform);
            a.playOnAwake = false;
            pool.Add(a);
            return a;
        }

        /// <summary>Plays a Minecraft sound event; at == null plays it as a UI sound (no position).</summary>
        public static void Play(string ev, Vector3? at = null, float volume = 1f, float pitch = 1f)
        {
            if (!McData.SoundEvents.TryGetValue(ev, out var list) || list.Count == 0) return;
            var pick = Pick(list, 0);
            if (pick == null) return;
            var file = (string)pick["file"];
            float v = volume * (pick["volume"] != null ? (float)pick["volume"] : 1f);
            float p = pitch * (pick["pitch"] != null ? (float)pick["pitch"] : 1f);
            if (!clips.TryGetValue(file, out var clip))
            {
                Load(file);
                if (runner != null) runner.StartCoroutine(PlayWhenLoaded(file, at, v, p));
                return;
            }
            PlayClip(clip, at, v, p);
        }

        private static JToken Pick(JArray list, int depth)
        {
            int total = 0;
            foreach (var s in list) total += s["weight"] != null ? (int)s["weight"] : 1;
            int r = Random.Range(0, Mathf.Max(1, total));
            foreach (var s in list)
            {
                r -= s["weight"] != null ? (int)s["weight"] : 1;
                if (r < 0)
                {
                    if (s["event"] != null && depth < 4)
                        return McData.SoundEvents.TryGetValue((string)s["event"], out var sub) ? Pick(sub, depth + 1) : null;
                    return s;
                }
            }
            return list[0];
        }

        private static IEnumerator PlayWhenLoaded(string file, Vector3? at, float v, float p)
        {
            float until = Time.unscaledTime + 1f;
            while (!clips.ContainsKey(file) && Time.unscaledTime < until) yield return null;
            if (clips.TryGetValue(file, out var c)) PlayClip(c, at, v, p);
        }

        private static void PlayClip(AudioClip clip, Vector3? at, float volume, float pitch)
        {
            if (clip == null) return;
            var a = Source();
            a.clip = clip;
            float master = 1f;
            try { master = SoundSystem.GetMasterVolume(); } catch { }
            a.volume = Mathf.Clamp01(volume) * master;
            a.pitch = pitch;
            if (at.HasValue)
            {
                a.transform.position = at.Value;
                a.spatialBlend = 1f;
                a.rolloffMode = AudioRolloffMode.Linear;
                a.minDistance = 1f;
                a.maxDistance = 16f * Mathf.Max(1f, volume); // Minecraft's attenuation distance
                a.dopplerLevel = 0f;
            }
            else a.spatialBlend = 0f;
            a.Play();
        }

        /// <summary>The sound group's event for a block: "block.&lt;group&gt;.&lt;kind&gt;" (break, step, place, hit, fall).</summary>
        public static string BlockEvent(string group, string kind)
        {
            var g = GroupEvent(group);
            var ev = "block." + g + "." + kind;
            return McData.SoundEvents.ContainsKey(ev) ? ev : "block.stone." + kind;
        }

        /// <summary>BlockSoundGroup field names map to sound event names (mostly identical, a few renamed).</summary>
        private static string GroupEvent(string g)
        {
            switch (g)
            {
                case "intentionally_empty": return "stone";
                case "lily_pad": return "lily_pad";
                case "metal": return "metal";
                case "small_amethyst_bud": return "small_amethyst_bud";
                case "wet_grass": return "wet_grass";
                case "nether_stem": return "stem";
                case "nether_wood": return "nether_wood";
                case "nylium": return "nylium";
                default: return g;
            }
        }
    }
}
