using System;
using System.Globalization;
using System.Linq;
using FiresCore.UI;
using FiresCore.Utilities;
using FiresDungeonMaster.Editor;
using HarmonyLib;
using UnityEngine;

namespace FiresDungeonMaster.UI
{
    /// <summary>
    /// 0.1.126: the environment-box preset editor, in the FAP context-menu look (Core FiresRoundedSkin, IMGUI). Pick or
    /// create a preset, set environment / biome / sky / visibility / wind / time / size, Save, and "Use for next box"
    /// queues it for the next EnvBox_vad placed. Opened from FAP's hammer right-click on the env box ("Env box presets...").
    /// </summary>
    public class EnvBoxPresetUI : MonoBehaviour
    {
        const int WindowId = 0x46D6;
        static EnvBoxPresetUI _instance;

        bool _open;
        Rect _rect;
        Vector2 _scroll;
        EnvBoxPreset _edit = new EnvBoxPreset();
        string _sx = "10", _sy = "5", _sz = "10";
        string _status = "";

        static readonly string[] Biomes = { "", "Meadows", "BlackForest", "Swamp", "Mountain", "Plains", "Mistlands", "AshLands", "DeepNorth", "Ocean" };

        public static bool IsOpen => _instance != null && _instance._open;

        public static void Open()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null || !EntryDoorController.IsAdmin()) return;
            if (_instance == null)
            {
                var go = new GameObject("FDM_EnvBoxPresetUI");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<EnvBoxPresetUI>();
            }
            _instance.OpenInternal();
        }

        void OpenInternal()
        {
            EnvBoxPresetStore.Load(true);
            Select(EnvBoxPresetStore.All.FirstOrDefault() ?? new EnvBoxPreset { Name = "New box" });
            float w = 540f, h = 600f;
            _rect = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            if (!_open) FiresCore.Input.FiresInputBlock.Acquire(this);
            _open = true;
            _status = "";
        }

        void Select(EnvBoxPreset p)
        {
            _edit = JsonUtility.FromJson<EnvBoxPreset>(JsonUtility.ToJson(p));
            _sx = F(_edit.Size.x); _sy = F(_edit.Size.y); _sz = F(_edit.Size.z);
        }

        void Close()
        {
            if (!_open) return;
            _open = false;
            FiresCore.Input.FiresInputBlock.Release(this);
        }

        void Update()
        {
            if (!_open) return;
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
            GUILayout.Label("Environment box presets", FiresRoundedSkin.Title);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("X", FiresRoundedSkin.ButtonSmall, GUILayout.Width(28f))) Close();
            GUILayout.EndHorizontal();

            _scroll = GUILayout.BeginScrollView(_scroll);
            GUILayout.Label("PRESETS", FiresRoundedSkin.SectionBar);
            GUILayout.BeginHorizontal();
            foreach (var p in EnvBoxPresetStore.All)
                if (GUILayout.Button(p.Name, p.Name == _edit.Name ? FiresRoundedSkin.NavSel : FiresRoundedSkin.NavItem)) Select(p);
            if (GUILayout.Button("+ New", FiresRoundedSkin.ButtonSmall)) Select(new EnvBoxPreset { Name = "New box" });
            GUILayout.EndHorizontal();

            GUILayout.Label("BOX", FiresRoundedSkin.SectionBar);
            _edit.Name = Field("Preset name", _edit.Name);
            _edit.Environment = Cycle("Environment", _edit.Environment, EntryDoorController.Environments);
            _edit.Environment = Field("   or any environment", _edit.Environment);
            _edit.Biome = Cycle("Biome (blank = keep)", _edit.Biome, Biomes);
            _edit.Skybox = Cycle("Skybox", _edit.Skybox, Enum.GetNames(typeof(EnvironmentBoxController.SkyboxMode)));
            _edit.Visibility = Cycle("Visibility", _edit.Visibility, Enum.GetNames(typeof(EnvironmentBoxController.VisibilityMode)));
            _edit.Wind = Cycle("Wind", _edit.Wind, Enum.GetNames(typeof(EnvironmentBoxController.WindIntensity)));
            _edit.TimeOfDay = Cycle("Time of day", _edit.TimeOfDay, Enum.GetNames(typeof(EnvironmentBoxController.ForcedTimeOfDay)));

            GUILayout.Label("SIZE (m)", FiresRoundedSkin.SectionBar);
            GUILayout.BeginHorizontal();
            GUILayout.Label("X", FiresRoundedSkin.Label, GUILayout.Width(16f)); _sx = GUILayout.TextField(_sx, FiresRoundedSkin.TextInput, GUILayout.Width(70f));
            GUILayout.Label("Y", FiresRoundedSkin.Label, GUILayout.Width(16f)); _sy = GUILayout.TextField(_sy, FiresRoundedSkin.TextInput, GUILayout.Width(70f));
            GUILayout.Label("Z", FiresRoundedSkin.Label, GUILayout.Width(16f)); _sz = GUILayout.TextField(_sz, FiresRoundedSkin.TextInput, GUILayout.Width(70f));
            GUILayout.EndHorizontal();
            GUILayout.EndScrollView();

            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status, FiresRoundedSkin.Hint);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save", FiresRoundedSkin.Button) && Commit()) { EnvBoxPresetStore.Save(_edit); _status = $"Saved '{_edit.Name}'."; }
            if (GUILayout.Button("Use for next box", FiresRoundedSkin.Button) && Commit())
            {
                EnvBoxPresetStore.Save(_edit);
                EnvBoxPresetQueue.Enqueue(_edit.Name);
                Close();
            }
            if (GUILayout.Button("Delete", FiresRoundedSkin.Button))
            {
                EnvBoxPresetStore.Delete(_edit.Name);
                _status = $"Deleted '{_edit.Name}'.";
                Select(new EnvBoxPreset { Name = "New box" });
            }
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0f, 0f, _rect.width, 32f));
        }

        bool Commit()
        {
            if (string.IsNullOrWhiteSpace(_edit.Name)) { _status = "Give the preset a name first."; return false; }
            if (!P(_sx, out float x) || !P(_sy, out float y) || !P(_sz, out float z) || x <= 0f || y <= 0f || z <= 0f)
            { _status = "Size: three numbers above 0."; return false; }
            _edit.Size = new Vector3(x, y, z);
            return true;
        }

        static bool P(string s, out float v) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        static string Field(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, FiresRoundedSkin.Label, GUILayout.Width(200f));
            string v = GUILayout.TextField(value ?? "", FiresRoundedSkin.TextInput);
            GUILayout.EndHorizontal();
            return v;
        }

        static string Cycle(string label, string value, string[] options)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, FiresRoundedSkin.Label, GUILayout.Width(200f));
            int i = Array.IndexOf(options, value);
            if (GUILayout.Button("<", FiresRoundedSkin.ButtonSmall, GUILayout.Width(28f))) value = options[(i <= 0 ? options.Length : i) - 1];
            GUILayout.Label(string.IsNullOrEmpty(value) ? "(keep)" : value, FiresRoundedSkin.Label, GUILayout.MinWidth(160f));
            if (GUILayout.Button(">", FiresRoundedSkin.ButtonSmall, GUILayout.Width(28f))) value = options[(i + 1) % options.Length];
            GUILayout.EndHorizontal();
            return value;
        }

        [HarmonyPatch(typeof(Player), "TakeInput")]
        static class Player_TakeInput_Patch
        {
            static void Postfix(ref bool __result) { if (IsOpen) __result = false; }
        }
    }
}
