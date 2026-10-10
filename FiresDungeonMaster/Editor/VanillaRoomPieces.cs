using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FiresDungeonMaster.Editor
{
    /// <summary>
    /// 0.1.124 (Fire: "dungeons should put all vanilla dungeon rooms on the hammer for placement as well, sorted by dungeon
    /// type"). Every enabled vanilla room in DungeonDB becomes an admin hammer piece, one hammer tab per dungeon type.
    /// A room piece is a small networked wrapper (ZNetView + Piece + <see cref="VanillaRoomVisual"/>) named
    /// "FDMRoom_&lt;room&gt;"; the room's own prefab is loaded from DungeonDB when the wrapper (or its ghost) appears and
    /// placed under it as STATIC geometry: the room's networked children (chests, spawners, doors) and its Room /
    /// connection markers are stripped, so a placed room never spawns or duplicates world objects.
    /// </summary>
    internal static class VanillaRoomPieces
    {
        internal const string Prefix = "FDMRoom_";

        // Hammer tab per dungeon type: label -> the Room.Theme bits whose rooms go there.
        internal static readonly (string Label, Room.Theme Themes)[] Tabs =
        {
            ("Crypt Rooms", Room.Theme.Crypt),
            ("Forest Crypt Rooms", Room.Theme.ForestCrypt | Room.Theme.ForestCryptHildir),
            ("Sunken Crypt Rooms", Room.Theme.SunkenCrypt),
            ("Cave Rooms", Room.Theme.Cave | Room.Theme.CaveHildir),
            ("Goblin Camp Rooms", Room.Theme.GoblinCamp | Room.Theme.PlainsFortHildir),
            ("Meadows Rooms", Room.Theme.MeadowsVillage | Room.Theme.MeadowsFarm),
            ("Dvergr Rooms", Room.Theme.DvergerTown | Room.Theme.DvergerBoss),
            ("Ashlands Ruins Rooms", Room.Theme.AshlandRuins),
            ("Fortress Rooms", Room.Theme.FortressRuins),
        };

        static GameObject _root;
        static readonly Dictionary<string, GameObject> _wrappers = new Dictionary<string, GameObject>(StringComparer.Ordinal);

        internal static bool IsRoomPiece(string prefabName) => prefabName != null && prefabName.StartsWith(Prefix, StringComparison.Ordinal);

        /// <summary>Creates (once) and registers the wrapper prefabs, then adds them to the hammer under their tab.</summary>
        internal static void AddToHammer(PieceTable table, Func<string, Piece.PieceCategory> categoryFor)
        {
            if (!DungeonMasterConfig.VanillaRoomsOnHammer.Value) { FiresLogger.LogVerbose("vanilla rooms on the hammer: off (config)"); return; }
            if (DungeonDB.instance == null || ZNetScene.instance == null || table == null)
            {
                Debug.Log("[FiresDungeonMaster] vanilla rooms on the hammer: skipped (DungeonDB/ZNetScene/hammer not ready)");
                return;
            }
            EnsureWrappers();
            var perTab = new Dictionary<string, int>();
            foreach (var kv in _wrappers)
            {
                var visual = kv.Value.GetComponent<VanillaRoomVisual>();
                string tab = TabFor(visual.Theme);
                if (tab == null || !DungeonMasterConfig.RoomTabEnabled(tab)) continue;
                var piece = kv.Value.GetComponent<Piece>();
                piece.m_category = categoryFor(tab);
                if (FiresCore.Pieces.HammerPieces.AddToTable(table, kv.Value))
                    perTab[tab] = perTab.TryGetValue(tab, out int n) ? n + 1 : 1;
            }
            Debug.Log($"[FiresDungeonMaster] vanilla rooms on the hammer: {perTab.Values.Sum()} room(s) in {perTab.Count} tab(s): " +
                      string.Join(", ", perTab.Select(p => $"{p.Key} {p.Value}")));
        }

        internal static string TabFor(Room.Theme theme)
        {
            foreach (var t in Tabs) if ((theme & t.Themes) != 0) return t.Label;
            return null;
        }

        static void EnsureWrappers()
        {
            var znet = ZNetScene.instance;
            if (_root == null)
            {
                _root = new GameObject("FDM_VanillaRoomPrefabs");
                _root.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_root);
                _wrappers.Clear();
            }
            foreach (DungeonDB.RoomData room in DungeonDB.GetRooms())
            {
                if (room == null || !room.m_enabled || room.m_prefab.Name == null) continue;
                if (TabFor(room.m_theme) == null) continue;
                string name = Prefix + room.m_prefab.Name;
                if (_wrappers.ContainsKey(name)) continue;

                var go = new GameObject(name);
                go.transform.SetParent(_root.transform, false);
                var nview = go.AddComponent<ZNetView>();
                nview.m_persistent = true;
                var piece = go.AddComponent<Piece>();
                piece.m_name = Pretty(room.m_prefab.Name);
                piece.m_description = $"Vanilla dungeon room ({TabFor(room.m_theme)}), placed as static geometry.";
                piece.m_resources = new Piece.Requirement[0];
                piece.m_enabled = true;
                piece.m_canBeRemoved = true;
                piece.m_clipEverything = true;
                piece.m_groundPiece = false;
                piece.m_allowedInDungeons = true;
                var visual = go.AddComponent<VanillaRoomVisual>();
                visual.RoomHash = room.Hash;
                visual.Theme = room.m_theme;
                _wrappers[name] = go;
                if (znet.GetPrefab(name) == null) FiresCore.World.NetworkPrefabs.RegisterByName(znet, go);
            }
        }

        static string Pretty(string roomName)
        {
            string s = roomName.Replace('_', ' ').Trim();
            return s.Length == 0 ? roomName : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
    }

    /// <summary>Loads the vanilla room under its wrapper as static geometry (also for the placement ghost).</summary>
    internal sealed class VanillaRoomVisual : MonoBehaviour
    {
        public int RoomHash;
        public Room.Theme Theme;

        DungeonDB.RoomData _data;
        GameObject _instance;

        void Start()
        {
            if (DungeonDB.instance == null) return;
            _data = DungeonDB.instance.GetRoom(RoomHash);
            if (_data == null) { Debug.LogWarning($"[FiresDungeonMaster] room piece {name}: room {RoomHash} not in DungeonDB"); return; }
            _data.m_prefab.Load();
            GameObject asset = _data.m_prefab.Asset;
            if (asset == null) { Debug.LogWarning($"[FiresDungeonMaster] room piece {name}: room asset did not load"); _data.m_prefab.Release(); _data = null; return; }

            bool was = asset.activeSelf;
            asset.SetActive(false);   // instantiate inactive so no child Awake (ZNetView, spawners) runs before stripping
            try { _instance = Instantiate(asset, transform, false); }
            finally { asset.SetActive(was); }
            _instance.name = "RoomGeometry";
            _instance.transform.localPosition = Vector3.zero;
            _instance.transform.localRotation = Quaternion.identity;
            int stripped = Strip(_instance);
            _instance.SetActive(true);
            FiresLogger.LogVerbose($"room piece {name}: geometry loaded, {stripped} networked/room component(s) stripped");
        }

        // Removes everything that would network, spawn or act: networked child objects go whole; Room / RoomConnection /
        // spawners go as components. What stays is meshes, colliders, lights and particle effects.
        static int Strip(GameObject root)
        {
            int n = 0;
            foreach (var nv in root.GetComponentsInChildren<ZNetView>(true))
            {
                if (nv == null) continue;
                if (nv.gameObject == root) { DestroyImmediate(nv); n++; continue; }
                DestroyImmediate(nv.gameObject);
                n++;
            }
            foreach (var c in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (c == null) continue;
                if (c is Room || c is RoomConnection || c is RandomSpawn || c is SpawnArea || c is CreatureSpawner
                    || c is DungeonGenerator || c is LocationProxy || c is Location)
                {
                    DestroyImmediate(c);
                    n++;
                }
            }
            return n;
        }

        void OnDestroy()
        {
            if (_data != null) _data.m_prefab.Release();
        }
    }
}
