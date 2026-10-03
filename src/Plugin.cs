using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
namespace WomenOfWasteland
{
    internal sealed class Runner : MonoBehaviour
    {
        private void Update()
        {
            Voice.EnsureLoading(this);
            try { Gungirl.Tick(); } catch (Exception e) { Plugin.Log.LogError("Female models: " + e); }
        }
    }
    [BepInPlugin(GUID, NAME, VERSION)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.denis.apocalypter.womenofwasteland", NAME = "Women of Wasteland", VERSION = "1.0.0";
        internal static ManualLogSource Log;
        internal static string Dir;
        internal static ConfigEntry<bool> Enabled, VerboseLog, GungirlVoiceMatch;
        internal static ConfigEntry<int> FemalePopulation;
        internal static ConfigEntry<float> GungirlVoiceVolume, GungirlVoiceIntervalMin, GungirlVoiceIntervalMax;
        internal static ConfigEntry<string> GungirlModel, GungirlTexture, GungirlVoice, GungirlHideParts, ShootaModel, ShootaTexture, ShootaHideParts;
        internal static ConfigEntry<Key> SpawnKey;
        private static ConfigFile _hidden;
        private static GameObject _runner;
        private static ConfigEntry<T> H<T>(string section, string key, T value, string description) { return _hidden.Bind(section, key, value, description); }
        private static ConfigEntry<T> H<T>(string section, string key, T value, ConfigDescription description) { return _hidden.Bind(section, key, value, description); }
        private void Awake()
        {
            bool existingConfig = File.Exists(Config.ConfigFilePath);
            Log = Logger; Dir = Path.GetDirectoryName(Info.Location);
            _hidden = new ConfigFile(Path.Combine(Paths.ConfigPath, "WomenOfWasteland/hidden-settings.not-saved"), false) { SaveOnConfigSet = false };
            Config.Bind("General", "Apocasetter", true, "Show this mod in the Apocasetter Mods menu");
            Enabled = Config.Bind("General", "EnableFemaleNpc", true, "Female raiders appear among the raiders. Off: no new ones; saved females keep their looks.");
            FemalePopulation = Config.Bind("General", "FemalePopulation", 50, new ConfigDescription("Percent of Flexa and Sprokka spawns converted to Gungirl and Shoota.", new AcceptableValueRange<int>(0, 100)));
            VerboseLog = Config.Bind("Debug", "VerboseLog", false, "Detailed female model and voice logs.");
            GungirlModel = Config.Bind("Gungirl", "Model", "Models/Flexa_female.glb",
                "Body model (.gltf or .glb), relative to the mod folder. Must be rigged to Flexa's skeleton (mixamorig bones). Read at game start.");
            GungirlTexture = Config.Bind("Gungirl", "Texture", "Models/flexa_female.png",
                "Body texture (PNG/JPG), relative to the mod folder. Read at game start.");
            GungirlVoice = H("Gungirl", "Voice", "Sounds/Gungirl",
                "Folder with her voice, relative to the mod folder: WAV or OGG files named like Flexa's clips (death_1, human_hurt, enemy_human_single_1 ...; .ogg wins over .wav of the same name). A clip without a file keeps Flexa's sound. Read at game start.");
            GungirlVoiceMatch = H("Gungirl", "VoiceMatchLoudness", true,
                "Bring each of her voice files to the loudness of the Flexa clip it replaces (quiet recordings get louder, a soft limiter stops distortion). Read at game start.");
            GungirlVoiceVolume = H("Gungirl", "VoiceVolume", 1f, new ConfigDescription(
                "Her voice volume on top of that: 1 = as loud as Flexa, 0.5 = half, 2 = twice (louder than 1.5 starts to sound squashed). Read at game start.",
                new AcceptableValueRange<float>(0f, 4f)));
            GungirlVoiceIntervalMin = H("Gungirl", "VoiceIntervalMin", 2f, new ConfigDescription(
                "In a fight she shouts, waits a random pause between VoiceIntervalMin and VoiceIntervalMax seconds, shouts again ... (Flexa: 0.1 to 4). Applies to Gungirls spawned or loaded after the change.",
                new AcceptableValueRange<float>(0f, 60f)));
            GungirlVoiceIntervalMax = H("Gungirl", "VoiceIntervalMax", 8f, new ConfigDescription(
                "Longest pause between her shouts, seconds (Flexa: 4).",
                new AcceptableValueRange<float>(0f, 60f)));
            GungirlHideParts = Config.Bind("Gungirl", "HideParts", "beard, headband, armband",
                "Flexa's attachments to hide on a Gungirl, comma-separated name starts: beard, headband, armband, bag1, pouch1, armor2, machete.");
            ShootaModel = Config.Bind("Shoota", "Model", "Models/Sprokka_female.glb",
                "Shoota (female Sprokka) body model (.gltf or .glb), relative to the mod folder, rigged to the human mixamorig skeleton. Read at game start.");
            ShootaTexture = Config.Bind("Shoota", "Texture", "Models/sprokka_female.png", "Shoota body texture (PNG/JPG), relative to the mod folder. Read at game start.");
            ShootaHideParts = Config.Bind("Shoota", "HideParts", "armor2, armband",
                "Sprokka's attachments to hide on a Shoota, comma-separated name starts: armor2 (the vest), armband, shiv.");

            SpawnKey = H("Debug", "SpawnKey", Key.None, "Debug: spawn a Gungirl 6 metres ahead. None = off.");
            LegacyConfig.Import(Config, Log, existingConfig);
            var harmony = new Harmony(GUID);
            harmony.Patch(AccessTools.Method(typeof(CreateObject), "OnEnter"), postfix: new HarmonyMethod(typeof(Gungirl), nameof(Gungirl.AfterCreateObject)));
            SceneManager.sceneLoaded += SceneLoaded;
            EnsureRunner(); Gungirl.OnSceneLoaded();
            Log.LogInfo(NAME + " " + VERSION + " loaded");
        }
        private void SceneLoaded(Scene scene, LoadSceneMode mode) { EnsureRunner(); Gungirl.OnSceneLoaded(); }
        private static void EnsureRunner()
        {
            if (_runner != null) return;
            _runner = new GameObject("WomenOfWasteland.Runner") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_runner); _runner.AddComponent<Runner>();
        }
        internal static void Verbose(string text) { if (VerboseLog != null && VerboseLog.Value && Log != null) Log.LogInfo(text); }
        internal static void Warn(string text) { if (Log != null) Log.LogWarning(text); }
    }
}