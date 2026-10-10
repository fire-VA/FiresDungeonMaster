using System;
using System.Linq;
using FiresCore.UI;
using FiresDungeonMaster.Editor;
using HarmonyLib;
using UnityEngine;

namespace FiresDungeonMaster.UI
{
    /// <summary>
    /// 0.1.125: the dungeon preset editor, in the FAP context-menu look (Core's FiresRoundedSkin, IMGUI, dragged by its
    /// title row). Pick or create a preset, set name / dungeon type / environment / lock, Save, and "Use for next door"
    /// queues it for the next entry door placed. Opened from FAP's hammer right-click ("Dungeon presets...") or the
    /// [Presets] "Dungeon presets key" (admin, hammer out).
    /// </summary>
    public class DungeonPresetUI : MonoBehaviour
    {
        const int WindowId = 0x46D5;
        static DungeonPresetUI _instance;

        bool _open;
        Rect _rect;
        Vector2 _scroll;
        string _door = "FiresDoorArched";
        DungeonPreset _edit = new DungeonPreset();
        string _status = "";

        public static bool IsOpen => _instance != null && _instance._open;

        /// <summary>Creates the (closed) window once, so its key works.</summary>
        public static void Ensure()
        {
            if (_instance != null) return;
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;   // headless server: no window
            var go = new GameObject("FDM_DungeonPresetUI");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<DungeonPresetUI>();
        }

        public static void Open(string doorPrefab)
        {
            Ensure();
            _instance.OpenInternal(doorPrefab);
        }

        void OpenInternal(string doorPrefab)
        {
            if (!EntryDoorController.IsAdmin()) return;
            if (!string.IsNullOrEmpty(doorPrefab)) _door = doorPrefab;
            DungeonPresetStore.Load(true);
            _edit = DungeonPresetStore.All.FirstOrDefault() is DungeonPreset first ? Copy(first) : new DungeonPreset { Name = "New dungeon" };
            float w = 560f, h = 640f;
            _rect = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            if (!_open) FiresCore.Input.FiresInputBlock.Acquire(this);
            _open = true;
            _status = "";
        }

        void Close()
        {
            if (!_open) return;
            _open = false;
            FiresCore.Input.FiresInputBlock.Release(this);
        }

        void Update()
        {
            if (!_open)
            {
                var key = DungeonMasterConfig.DungeonPresetsKey;
                if (key != null && key.Value.IsDown() && Player.m_localPlayer != null && Player.m_localPlayer.InPlaceMode()) Open(null);
                return;
            }
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            if (Input.GetKeyDown(KeyCode.Escape)) Close();
        }

        void OnGUI()
        {
            if (!_open) return;
            FiresRoundedSkin.Ensure(1f);
            _rect = GUI.Window(WindowId, _rect, Draw, "", FiresRoundedSkin.Window);
            FiresRoundedSkin.DrawPendingTooltip();
        }

        void Draw(int id)
        {
            FiresRoundedSkin.ResetTooltip();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Dungeon presets", FiresRoundedSkin.Title);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("X", FiresRoundedSkin.ButtonSmall, GUILayout.Width(28f))) Close();
            GUILayout.EndHorizontal();

            _scroll = GUILayout.BeginScrollView(_scroll);
            GUILayout.Label("PRESETS", FiresRoundedSkin.SectionBar);
            GUILayout.BeginHorizontal();
            foreach (var p in DungeonPresetStore.All)
                if (GUILayout.Button(p.Name, p.Name == _edit.Name ? FiresRoundedSkin.NavSel : FiresRoundedSkin.NavItem)) _edit = Copy(p);
            if (GUILayout.Button("+ New", FiresRoundedSkin.ButtonSmall)) _edit = new DungeonPreset { Name = "New dungeon" };
            GUILayout.EndHorizontal();

            GUILayout.Label("DUNGEON", FiresRoundedSkin.SectionBar);
            _edit.Name = Field("Preset name", _edit.Name);
            _edit.DisplayName = Field("Display name (entry banner)", _edit.DisplayName);
            _edit.DungeonType = Cycle("Dungeon type", _edit.DungeonType, EntryDoorController.VanillaDungeons);
            _edit.DungeonType = Field("   or a custom type", _edit.DungeonType);
            _edit.IsVanilla = Array.IndexOf(EntryDoorController.VanillaDungeons, _edit.DungeonType) >= 0;
            _edit.EmptyBox = GUILayout.Toggle(_edit.EmptyBox, " Empty room (no generated rooms)", FiresRoundedSkin.Label);
            _edit.Environment = Cycle("Environment", _edit.Environment, EntryDoorController.Environments);

            GUILayout.Label("LOCK", FiresRoundedSkin.SectionBar);
            string[] locks = Enum.GetNames(typeof(EntryDoorController.DoorLockMode));
            _edit.Lock = Array.IndexOf(locks, Cycle("Lock", locks[Mathf.Clamp(_edit.Lock, 0, locks.Length - 1)], locks));
            if (_edit.Lock == (int)EntryDoorController.DoorLockMode.Key)
            {
                _edit.KeyPrefab = Cycle("Key", _edit.KeyPrefab, EntryDoorController.KeyOptions);
                _edit.LockDifficulty = Cycle("Pick difficulty", _edit.LockDifficulty, EntryDoorController.LockDifficulties);
            }
            if (_edit.Lock == (int)EntryDoorController.DoorLockMode.Quest) _edit.QuestId = Field("Quest id", _edit.QuestId);
            GUILayout.EndScrollView();

            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status, FiresRoundedSkin.Hint);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save", FiresRoundedSkin.Button))
            {
                if (string.IsNullOrWhiteSpace(_edit.Name)) _status = "Give the preset a name first.";
                else { DungeonPresetStore.Save(Copy(_edit)); _status = $"Saved '{_edit.Name}'."; }
            }
            if (GUILayout.Button($"Use for next {Pretty(_door)}", FiresRoundedSkin.Button))
            {
                if (string.IsNullOrWhiteSpace(_edit.Name)) _status = "Give the preset a name first.";
                else { DungeonPresetStore.Save(Copy(_edit)); DungeonPresetQueue.Enqueue(_door, _edit.Name); Close(); }
            }
            if (GUILayout.Button("Delete", FiresRoundedSkin.Button))
            {
                DungeonPresetStore.Delete(_edit.Name);
                _status = $"Deleted '{_edit.Name}'.";
                _edit = new DungeonPreset { Name = "New dungeon" };
            }
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0f, 0f, _rect.width, 32f));
        }

        static string Field(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, FiresRoundedSkin.Label, GUILayout.Width(200f));
            string v = GUILayout.TextField(value ?? "", FiresRoundedSkin.TextInput);
            GUILayout.EndHorizontal();
            return v;
        }

        // A value from a list, stepped with < >; an unknown value (a custom type) shows as is.
        static string Cycle(string label, string value, string[] options)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, FiresRoundedSkin.Label, GUILayout.Width(200f));
            int i = Array.IndexOf(options, value);
            if (GUILayout.Button("<", FiresRoundedSkin.ButtonSmall, GUILayout.Width(28f))) value = options[(i <= 0 ? options.Length : i) - 1];
            GUILayout.Label(value ?? "", FiresRoundedSkin.Label, GUILayout.MinWidth(160f));
            if (GUILayout.Button(">", FiresRoundedSkin.ButtonSmall, GUILayout.Width(28f))) value = options[(i + 1) % options.Length];
            GUILayout.EndHorizontal();
            return value;
        }

        static string Pretty(string door) => door.Replace("FiresDoor", "").Replace("Medieval", "medieval door ").Replace("Arched", "arched door");

        static DungeonPreset Copy(DungeonPreset p) => JsonUtility.FromJson<DungeonPreset>(JsonUtility.ToJson(p));

        [HarmonyPatch(typeof(Player), "TakeInput")]
        static class Player_TakeInput_Patch
        {
            static void Postfix(ref bool __result) { if (IsOpen) __result = false; }
        }
    }
}
