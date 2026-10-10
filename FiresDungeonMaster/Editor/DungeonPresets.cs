using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresDungeonMaster.Editor
{
    /// <summary>
    /// 0.1.125 (Fire: "dungeons will get a right click preset option, like our fap presets, that give us the ability to setup
    /// custom names, env, locked/unlocked/etc from the preset ui"). A dungeon preset is the entry door's whole config;
    /// right-clicking a dungeon door on the hammer lists the presets ("Dungeon preset: X") through FAP's FapPieceMenu hook,
    /// picking one queues it for the NEXT door placed, and the door takes it on placement (Piece.SetCreator). Presets are
    /// JSON files in BepInEx/config/FiresDungeonMaster/DungeonPresets. Editing a placed door afterwards is unchanged.
    /// </summary>
    [Serializable]
    public class DungeonPreset
    {
        public string Name = "";
        public string DungeonType = "Forest Crypt";
        public bool IsVanilla = true;
        public bool EmptyBox;
        public string DisplayName = "";
        public string Environment = "Crypt";
        public int Lock;                         // EntryDoorController.DoorLockMode
        public string KeyPrefab = "FiresKeyOld";
        public string LockDifficulty = "Intermediate";
        public string QuestId = "";

        internal void ApplyTo(EntryDoorController door)
        {
            door.DungeonType = DungeonType ?? "";
            door.IsVanilla = IsVanilla;
            door.EmptyBox = EmptyBox;
            door.DisplayName = DisplayName ?? "";
            door.Environment = string.IsNullOrEmpty(Environment) ? "Crypt" : Environment;
            door.Lock = (EntryDoorController.DoorLockMode)Mathf.Clamp(Lock, 0, 2);
            door.KeyPrefab = KeyPrefab ?? "";
            door.LockDifficulty = LockDifficulty ?? "Intermediate";
            door.QuestId = QuestId ?? "";
        }
    }

    internal static class DungeonPresetStore
    {
        static readonly Dictionary<string, DungeonPreset> _presets = new Dictionary<string, DungeonPreset>(StringComparer.OrdinalIgnoreCase);
        static bool _loaded;

        internal static string Folder => Path.Combine(BepInEx.Paths.ConfigPath, "FiresDungeonMaster", "DungeonPresets");

        internal static IEnumerable<DungeonPreset> All { get { Load(); return _presets.Values.OrderBy(p => p.Name); } }

        internal static bool TryGet(string name, out DungeonPreset p) { Load(); return _presets.TryGetValue(name ?? "", out p); }

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
                        var p = JsonUtility.FromJson<DungeonPreset>(File.ReadAllText(f));
                        if (p != null && !string.IsNullOrEmpty(p.Name)) _presets[p.Name] = p;
                    }
                    catch (Exception ex) { Debug.LogWarning($"[FiresDungeonMaster] dungeon preset {Path.GetFileName(f)} unreadable: {ex.Message}"); }
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[FiresDungeonMaster] dungeon presets: {ex.Message}"); }
        }

        internal static void Save(DungeonPreset p)
        {
            if (p == null || string.IsNullOrEmpty(p.Name)) return;
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, SafeFile(p.Name) + ".json"), JsonUtility.ToJson(p, true));
            _presets[p.Name] = p;
            FiresLogger.LogVerbose($"dungeon preset saved: {p.Name}");
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

    /// <summary>One queued preset, for the next entry door this client places.</summary>
    internal static class DungeonPresetQueue
    {
        internal static string Pending;
        internal static string PendingDoorPrefab;

        internal static void Enqueue(string doorPrefab, string preset)
        {
            Pending = preset;
            PendingDoorPrefab = doorPrefab;
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, $"Next {doorPrefab}: dungeon preset '{preset}'");
            Debug.Log($"[FiresDungeonMaster] dungeon preset '{preset}' queued for the next {doorPrefab}");
        }

        [HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator))]
        static class Piece_SetCreator_DungeonPreset_Patch
        {
            static void Postfix(Piece __instance)
            {
                if (string.IsNullOrEmpty(Pending) || __instance == null) return;
                if (Utils.GetPrefabName(__instance.gameObject) != PendingDoorPrefab) return;
                var door = __instance.GetComponent<EntryDoorController>();
                if (door == null) return;
                string name = Pending;
                Pending = null;
                PendingDoorPrefab = null;
                if (!DungeonPresetStore.TryGet(name, out var p))
                {
                    Debug.LogWarning($"[FiresDungeonMaster] queued dungeon preset '{name}' no longer exists; the door keeps its defaults");
                    return;
                }
                p.ApplyTo(door);
                Debug.Log($"[FiresDungeonMaster] dungeon preset '{name}' applied to {__instance.name}: {p.DungeonType}, env {p.Environment}, " +
                          $"name '{p.DisplayName}', lock {(EntryDoorController.DoorLockMode)p.Lock}");
            }
        }
    }

    /// <summary>
    /// Hooks the presets into FAP's build-menu right-click (VerdantsAscent.Api.FapPieceMenu, FAP 1.1.200+) by reflection;
    /// without that hook the presets are reached through the Dungeon Presets panel's own key.
    /// </summary>
    internal static class DungeonPresetMenu
    {
        internal static readonly HashSet<string> DoorPrefabs = new HashSet<string>(StringComparer.Ordinal)
        { "FiresDoorMedievalA", "FiresDoorMedievalB", "FiresDoorMedievalC", "FiresDoorArched" };

        internal static void TryRegister()
        {
            Type api = null;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { api = asm.GetType("VerdantsAscent.Api.FapPieceMenu", false); } catch { }
                if (api != null) break;
            }
            MethodInfo register = api?.GetMethod("RegisterProvider", BindingFlags.Public | BindingFlags.Static);
            if (register == null)
            {
                Debug.Log("[FiresDungeonMaster] dungeon presets: FAP's FapPieceMenu hook not found (FAP missing or older than 1.1.200); " +
                          "use the Dungeon Presets key instead");
                return;
            }
            Func<Piece, IEnumerable<KeyValuePair<string, Action>>> provider = Entries;
            register.Invoke(null, new object[] { "FDM", provider });
            Debug.Log("[FiresDungeonMaster] dungeon presets: on FAP's hammer right-click menu");
        }

        static IEnumerable<KeyValuePair<string, Action>> Entries(Piece piece)
        {
            if (piece == null) return null;
            string prefab = Utils.GetPrefabName(piece.gameObject);
            // 0.1.126: the environment box gets its presets on the same menu.
            if (prefab == EnvBoxPresetQueue.EnvBoxPrefab && EntryDoorController.IsAdmin())
            {
                var boxEntries = new List<KeyValuePair<string, Action>>();
                foreach (var bp in EnvBoxPresetStore.All)
                {
                    string bname = bp.Name;
                    boxEntries.Add(new KeyValuePair<string, Action>($"Env box preset: {bname}", () => EnvBoxPresetQueue.Enqueue(bname)));
                }
                boxEntries.Add(new KeyValuePair<string, Action>("Env box presets...", () => UI.EnvBoxPresetUI.Open()));
                return boxEntries;
            }
            if (!DoorPrefabs.Contains(prefab) || !EntryDoorController.IsAdmin()) return null;
            var list = new List<KeyValuePair<string, Action>>();
            foreach (var p in DungeonPresetStore.All)
            {
                string name = p.Name;
                list.Add(new KeyValuePair<string, Action>($"Dungeon preset: {name}", () => DungeonPresetQueue.Enqueue(prefab, name)));
            }
            list.Add(new KeyValuePair<string, Action>("Dungeon presets...", () => UI.DungeonPresetUI.Open(prefab)));
            return list;
        }
    }
}
