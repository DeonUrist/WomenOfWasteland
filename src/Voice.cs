using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.Networking;

namespace WomenOfWasteland
{
    // Gungirl's own voice. Flexa's voice clips (asset scan 2026-10-01):
    //   Sound  [attack]    ArrayGetRandom array    enemy_human_single_1..8  (shouts while attacking)
    //   Sound3 [playAudio] ArrayGetRandom array    human_hurt, human_hurt_2 (hit)
    //   Health [spawn]     PlayRandomSound clips   death_1, death_3, death_6 (death)
    // These clips are shared with other humans (human_hurt also with the player), so the clips themselves are never touched:
    // only the references inside one Gungirl's own FSMs (action fields, FSM variables) and AudioSources are pointed at our
    // clips. A file <clip name>.wav or .ogg in [Gungirl] Voice replaces that clip; clips without a file keep the game's sound.
    internal static class Voice
    {
        private struct Pending { public GameObject Go; public float Until; }

        private static readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, AudioClip> _ready = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);   // leveled
        private static bool _loaded;
        private static MonoBehaviour _loader;     // the runner the load coroutine runs on (null = not started / runner destroyed)
        private static readonly List<Pending> _pending = new List<Pending>();
        private static readonly Dictionary<Type, FieldInfo[]> _fields = new Dictionary<Type, FieldInfo[]>();

        // Loads every .wav and .ogg once per game start. WAV is decoded here (Wav.cs); OGG by Unity's own decoder through a
        // file:// UnityWebRequest, which needs a coroutine. Same name in both formats: the .ogg wins (the shipped .wav files
        // are Flexa's originals, a .ogg next to one is the replacement).
        public static void EnsureLoading(MonoBehaviour runner)
        {
            if (_loaded || _loader != null) return;
            _loader = runner;
            runner.StartCoroutine(Load());
        }

        private static IEnumerator Load()
        {
            _ready.Clear();     // clips already read by a load that was cut short (runner destroyed) are kept, not re-created
            string dir = Gungirl.ModPath(Plugin.GungirlVoice.Value);
            if (!Directory.Exists(dir))
            {
                Plugin.Log.LogInfo("Gungirl voice: no folder " + dir + " - she keeps Flexa's voice");
                _loaded = true;
                yield break;
            }
            var failed = new List<string>();
            int wavs = 0, oggs = 0;
            foreach (var path in Directory.GetFiles(dir, "*.wav"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (_clips.ContainsKey(name)) { wavs++; continue; }
                try
                {
                    int ch, rate;
                    float[] s = Wav.Read(File.ReadAllBytes(path), out ch, out rate);
                    var clip = AudioClip.Create(name, Math.Max(1, s.Length / ch), ch, rate, false);
                    clip.SetData(s, 0);
                    clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    _clips[name] = clip;
                    wavs++;
                }
                catch (Exception e) { failed.Add(Path.GetFileName(path) + " (" + e.Message + ")"); }
                yield return null;      // one file per frame: no load hitch
            }
            foreach (var path in Directory.GetFiles(dir, "*.ogg"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                var req = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.OGGVORBIS);
                var dh = req.downloadHandler as DownloadHandlerAudioClip;
                if (dh != null) { dh.streamAudio = false; dh.compressed = false; }   // short clips: decode fully on load
                yield return req.SendWebRequest();
                try
                {
                    if (req.result != UnityWebRequest.Result.Success) throw new IOException(req.error);
                    var clip = DownloadHandlerAudioClip.GetContent(req);
                    if (clip == null || clip.length <= 0f) throw new InvalidDataException("not a readable Ogg Vorbis file");
                    clip.name = name;
                    clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    AudioClip old;
                    if (_clips.TryGetValue(name, out old) && old != null && old != clip) UnityEngine.Object.Destroy(old);   // the .wav it replaces
                    _clips[name] = clip;
                    oggs++;
                }
                catch (Exception e) { failed.Add(Path.GetFileName(path) + " (" + e.Message + ")"); }
                finally { req.Dispose(); }
            }
            Plugin.Log.LogInfo("Gungirl voice: " + _clips.Count + " clip(s) from " + dir + " (" + wavs + " wav, " + oggs + " ogg)");
            if (failed.Count > 0) Plugin.Log.LogWarning("Gungirl voice: could not read " + string.Join(", ", failed.ToArray()));
            _loaded = true;
        }

        // Called when a body is dressed. FSM actions are only safe to edit once PlayMaker has initialised the FSM,
        // so a fresh spawn may have to wait a frame or two; and the clips may still be loading at game start.
        public static void Apply(GameObject root)
        {
            if (root == null) return;
            if (!_loaded || !TrySwap(root, false)) _pending.Add(new Pending { Go = root, Until = Time.unscaledTime + 10f });
        }

        public static void Tick()
        {
            if (!_loaded) return;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var p = _pending[i];
                if (p.Go == null) { _pending.RemoveAt(i); continue; }
                if (TrySwap(p.Go, Time.unscaledTime > p.Until)) _pending.RemoveAt(i);
            }
        }

        private static bool TrySwap(GameObject root, bool force)
        {
            var fsms = root.GetComponentsInChildren<PlayMakerFSM>(true);
            if (!force)
                foreach (var f in fsms)
                    if (f != null && f.gameObject.activeInHierarchy && (f.Fsm == null || !f.Fsm.Initialized)) return false;
            int n = 0, timers = 0;
            foreach (var f in fsms)
                if (f != null && f.Fsm != null && f.Fsm.Initialized)
                {
                    if (_clips.Count > 0) n += SwapFsm(f.Fsm);
                    if (f.FsmName == "Sound") timers += SetPause(f.Fsm);
                }
            if (_clips.Count > 0)
            foreach (var a in root.GetComponentsInChildren<AudioSource>(true))
            {
                var r = Rep(a.clip);
                if (r != null) { a.clip = r; n++; }
            }
            int labels = Gungirl.Label(root);
            Plugin.Verbose("Gungirl voice: " + root.name + " - " + n + " sound reference(s) replaced, " + timers + " shout timer(s) set" + (labels > 0 ? ", label set" : ""));
            return true;
        }

        // Flexa's Sound FSM (global Animal_Run -> randomWait): attack = play a random shout, wait for its end -> randomWait
        // (RandomWait 0.1..4 s) -> attack ... for as long as she fights. [Gungirl] VoiceIntervalMin/Max replace that pause.
        private static int SetPause(Fsm fsm)
        {
            float lo = Mathf.Max(0f, Plugin.GungirlVoiceIntervalMin.Value), hi = Mathf.Max(lo, Plugin.GungirlVoiceIntervalMax.Value);
            int n = 0;
            if (fsm.States == null) return 0;
            foreach (var st in fsm.States)
            {
                if (st.Name != "randomWait") continue;
                FsmStateAction[] acts;
                try { acts = st.Actions; } catch (Exception) { continue; }
                if (acts == null) continue;
                foreach (var a in acts)
                {
                    var rw = a as HutongGames.PlayMaker.Actions.RandomWait;
                    if (rw == null) continue;
                    rw.min = new FsmFloat { Value = lo };
                    rw.max = new FsmFloat { Value = hi };
                    n++;
                }
            }
            return n;
        }

        private static int SwapFsm(Fsm fsm)
        {
            int n = 0;
            var vars = fsm.Variables;
            if (vars != null)
            {
                if (vars.ObjectVariables != null) foreach (var v in vars.ObjectVariables) n += SwapObj(v);
                if (vars.ArrayVariables != null) foreach (var a in vars.ArrayVariables) n += SwapArr(a);
            }
            if (fsm.States == null) return n;
            foreach (var st in fsm.States)
            {
                FsmStateAction[] acts;
                try { acts = st.Actions; } catch (Exception) { continue; }
                if (acts == null) continue;
                foreach (var act in acts)
                {
                    if (act == null) continue;
                    foreach (var fi in FieldsOf(act.GetType()))
                    {
                        object v = fi.GetValue(act);
                        if (v == null) continue;
                        var clip = v as AudioClip;
                        if (clip != null) { var r = Rep(clip); if (r != null) { fi.SetValue(act, r); n++; } continue; }
                        var clips = v as AudioClip[];
                        if (clips != null) { for (int i = 0; i < clips.Length; i++) { var r = Rep(clips[i]); if (r != null) { clips[i] = r; n++; } } continue; }
                        var fo = v as FsmObject;
                        if (fo != null) { n += SwapObj(fo); continue; }
                        var fos = v as FsmObject[];
                        if (fos != null) { foreach (var o in fos) n += SwapObj(o); continue; }
                        var fa = v as FsmArray;
                        if (fa != null) n += SwapArr(fa);
                    }
                }
            }
            return n;
        }

        private static FieldInfo[] FieldsOf(Type t)
        {
            FieldInfo[] f;
            if (_fields.TryGetValue(t, out f)) return f;
            var l = new List<FieldInfo>();
            for (var tt = t; tt != null && tt != typeof(FsmStateAction) && tt != typeof(object); tt = tt.BaseType)
                foreach (var fi in tt.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var ft = fi.FieldType;
                    if (ft == typeof(AudioClip) || ft == typeof(AudioClip[]) || ft == typeof(FsmObject) || ft == typeof(FsmObject[]) || ft == typeof(FsmArray))
                        l.Add(fi);
                }
            f = l.ToArray();
            _fields[t] = f;
            return f;
        }

        private static AudioClip Rep(UnityEngine.Object o)
        {
            var ac = o as AudioClip;
            if (ac == null) return null;
            AudioClip r;
            if (_ready.TryGetValue(ac.name, out r)) return r != ac ? r : null;
            if (!_clips.TryGetValue(ac.name, out r) || r == ac) return null;
            r = Leveled(ac, r);
            _ready[ac.name] = r;
            return r;
        }

        // Once per clip: [Gungirl] VoiceMatchLoudness brings the replacement to the loudness of the game clip it replaces
        // (the game's voice clips peak at 0 dBFS, home recordings are often 5-15 dB quieter), then [Gungirl] VoiceVolume
        // scales it; a soft limiter keeps the peaks below full scale. The result is a new clip (the loaded one stays as read).
        private static AudioClip Leveled(AudioClip original, AudioClip mine)
        {
            try
            {
                bool match = Plugin.GungirlVoiceMatch.Value;
                double volume = Plugin.GungirlVoiceVolume.Value;
                if (!match && Math.Abs(volume - 1.0) < 1e-4) return mine;
                var d = new float[mine.samples * mine.channels];
                if (d.Length == 0 || !mine.GetData(d, 0)) return mine;
                double target = 0;
                if (match)
                {
                    if (original.loadState != AudioDataLoadState.Loaded) original.LoadAudioData();
                    var o = new float[original.samples * original.channels];
                    if (o.Length > 0 && original.GetData(o, 0)) target = Level.ActiveRms(o);
                    else Plugin.Log.LogWarning("Gungirl voice: could not read the game's " + original.name + " to match its loudness (only VoiceVolume applies)");
                }
                int limited;
                double gain = Level.Process(d, target, volume, out limited);
                if (Math.Abs(gain - 1.0) < 1e-4) return mine;
                var c = AudioClip.Create(mine.name, mine.samples, mine.channels, mine.frequency, false);
                c.SetData(d, 0);
                c.hideFlags = HideFlags.DontUnloadUnusedAsset;
                Plugin.Log.LogInfo(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Gungirl voice: {0} {1:+0.0;-0.0} dB{2}{3}", mine.name, 20.0 * Math.Log10(gain),
                    target > 0 ? " (matched to the game's clip)" : "",
                    limited > 0 ? string.Format(System.Globalization.CultureInfo.InvariantCulture, ", {0:0.0} % of samples limited", 100.0 * limited / d.Length) : ""));
                return c;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Gungirl voice: leveling " + mine.name + " failed, used as is: " + e.Message);
                return mine;
            }
        }

        private static int SwapObj(FsmObject v)
        {
            if (v == null) return 0;
            var r = Rep(v.Value);
            if (r == null) return 0;
            v.Value = r;
            return 1;
        }

        private static int SwapArr(FsmArray a)
        {
            if (a == null) return 0;
            object[] vals;
            try { vals = a.Values; } catch (Exception) { return 0; }
            if (vals == null) return 0;
            int n = 0;
            for (int i = 0; i < vals.Length; i++)
            {
                var r = Rep(vals[i] as UnityEngine.Object);
                if (r != null) { a.Set(i, r); n++; }
            }
            if (n > 0) a.SaveChanges();
            return n;
        }
    }
}
