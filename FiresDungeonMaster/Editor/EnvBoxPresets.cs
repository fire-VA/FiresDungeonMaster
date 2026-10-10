using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FiresCore.Utilities;
using HarmonyLib;
using UnityEngine;

namespace FiresDungeonMaster.Editor
{
    /// <summary>
    /// 0.1.126 (Fire: "our env boxes should also get a preset setup and ui so we can set the presets right from the hammer
    /// menu and queue them for placement directly from there, instead of having to place them and then shift alt right
    /// click to adjust them"). An env-box preset holds the box's environment, biome, sky, visibility, wind, time and size;
    /// right-click EnvBox_vad on the hammer (FAP's FapPieceMenu) lists them, picking one queues it, and the next env box
    /// placed takes it (Piece.SetCreator, through the box's own Set* methods). Shift+Alt+right-click editing stays.
    /// JSON files in BepInEx/config/FiresDungeonMaster/EnvBoxPresets.
    /// </summary>
    [Serializable]
    public class EnvBoxPreset
    {
        public string Name = "";
        public string Environment = "Crypt";
        public string Biome = "";                // "" = leave the biome alone
        public string Skybox = nameof(EnvironmentBoxController.SkyboxMode.Inherit);
        public string Visibility = nameof(EnvironmentBoxController.VisibilityMode.Full);
        public string Wind = nameof(EnvironmentBoxController.WindIntensity.Inherit);
        public string TimeOfDay = nameof(EnvironmentBoxController.ForcedTimeOfDay.Inherit);
        public Vector3 Size = new Vector3(10f, 5f, 10f);

        internal void ApplyTo(EnvironmentBoxController box)
        {
            if (!string.IsNullOrEmpty(Environment)) box.SetEnvironment(Environment);
            if (!string.IsNullOrEmpty(Biome) && Enum.TryParse(Biome, out Heightmap.Biome b)) box.SetBiome(b);
            if (Enum.TryParse(Skybox, out EnvironmentBoxController.SkyboxMode s)) box.SetSkyboxMode(s);
            if (Enum.TryParse(Visibility, out EnvironmentBoxController.VisibilityMode v)) box.SetVisibility(v);
            if (Enum.TryParse(Wind, out EnvironmentBoxController.WindIntensity w)) box.SetWindIntensity(w);
            if (Enum.TryParse(TimeOfDay, out EnvironmentBoxController.ForcedTimeOfDay t)) box.SetTimeOfDay(t);
            if (Size.x > 0f && Size.y > 0f && Size.z > 0f) box.SetSize(Size);
        }
    }

    internal static class EnvBoxPresetStore
    {
        static readonly Dictionary<string, EnvBoxPreset> _presets = new Dictionary<string, EnvBoxPreset>(StringComparer.OrdinalIgnoreCase);
        static bool _loaded;

        internal static string Folder => Path.Combine(BepInEx.Paths.ConfigPath, "FiresDungeonMaster", "EnvBoxPresets");
        internal static IEnumerable<EnvBoxPreset> All { get { Load(); return _presets.Values.OrderBy(p => p.Name); } }
        internal static bool TryGet(string name, out EnvBoxPreset p) { Load(); return _presets.TryGetValue(name ?? "", out p); }

        internal static void Load(bool force = false)
        {
            if (_loaded && !force) return;
            _loaded = true;
            _presets.Clear();
            try
            {
                Directory.CreateDirectory(Folder);
                foreach (string f in Directory.GetFiles(Folder, "*.json"))
                {
                    try
                    {
                        var p = JsonUtility.FromJson<EnvBoxPreset>(File.ReadAllText(f));
                        if (p != null && !string.IsNullOrEmpty(p.Name)) _presets[p.Name] = p;
                    }
                    catch (Exception ex) { Debug.LogWarning($"[FiresDungeonMaster] env-box preset {Path.GetFileName(f)} unreadable: {ex.Message}"); }
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[FiresDungeonMaster] env-box presets: {ex.Message}"); }
        }

        internal static void Save(EnvBoxPreset p)
        {
            if (p == null || string.IsNullOrEmpty(p.Name)) return;
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, SafeFile(p.Name) + ".json"), JsonUtility.ToJson(p, true));
            _presets[p.Name] = p;
        }

        internal static void Delete(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            string f = Path.Combine(Folder, SafeFile(name) + ".json");
            if (File.Exists(f)) File.Delete(f);
            _presets.Remove(name);
        }

        static string SafeFile(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }

    internal static class EnvBoxPresetQueue
    {
        internal const string EnvBoxPrefab = "EnvBox_vad";
        internal static string Pending;

        internal static void Enqueue(string preset)
        {
            Pending = preset;
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, $"Next environment box: preset '{preset}'");
            Debug.Log($"[FiresDungeonMaster] env-box preset '{preset}' queued for the next {EnvBoxPrefab}");
        }

        [HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator))]
        static class Piece_SetCreator_EnvBoxPreset_Patch
        {
            static void Postfix(Piece __instance)
            {
                if (string.IsNullOrEmpty(Pending) || __instance == null) return;
                if (Utils.GetPrefabName(__instance.gameObject) != EnvBoxPrefab) return;
                var box = __instance.GetComponent<EnvironmentBoxController>();
                if (box == null) return;
                string name = Pending;
                Pending = null;
                if (!EnvBoxPresetStore.TryGet(name, out var p))
                {
                    Debug.LogWarning($"[FiresDungeonMaster] queued env-box preset '{name}' no longer exists; the box keeps its defaults");
                    return;
                }
                p.ApplyTo(box);
                Debug.Log($"[FiresDungeonMaster] env-box preset '{name}' applied: env {p.Environment}, sky {p.Skybox}, visibility {p.Visibility}, " +
                          $"wind {p.Wind}, time {p.TimeOfDay}, size {p.Size}");
            }
        }
    }
}
