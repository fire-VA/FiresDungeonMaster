using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using TMPro;
using FiresCore.UI;
using FiresDungeonMaster.Editor;

namespace FiresDungeonMaster.UI
{
    /// <summary>
    /// SaveSectionInput — the "Save room set / Save blueprint" naming dialog. Opened from the notebook center editor
    /// instead of saving straight away: the admin types a NAME (display + id key), an optional dungeon TYPE, and
    /// optional NOTES/tags, then Save writes the .blueprint(s). Built on a standalone top-level ScreenSpaceOverlay
    /// canvas (per reference_standalone_canvas), parchment fields via UIBuilderHelper, sorted ABOVE the notebook so
    /// it is never hidden behind it. Client-only (guards on IsClientOnly).
    /// </summary>
    public static class SaveSectionInput
    {
        private static GameObject _canvasGo;
        private static TMP_InputField _nameField, _typeField, _notesField;
        private static SectionFootprintController _target;

        private static bool IsClientOnly() => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

        public static void Open(SectionFootprintController target)
        {
            if (IsClientOnly() || target == null) return;
            Close();
            DungeonTypeInput.Close();   // dismiss any lingering "New Dungeon Type" popup so it can't be revealed behind us
            _target = target;
            Build();
        }

        private static void Build()
        {
            _canvasGo = new GameObject("FDM_SaveSectionInput");
            Object.DontDestroyOnLoad(_canvasGo);
            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 7500;   // above the notebook (6300+) so it's usable, not hidden behind it
            var scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _canvasGo.AddComponent<GraphicRaycaster>();

            var panelGo = new GameObject("Panel");
            panelGo.transform.SetParent(_canvasGo.transform, false);
            var panelRt = panelGo.AddComponent<RectTransform>();
            panelRt.sizeDelta = new Vector2(560f, 340f);
            panelRt.anchorMin = panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.anchoredPosition = Vector2.zero;
            panelGo.AddComponent<Image>().color = UIFontConfig.Colors.ParchmentPanel;
            var ol = panelGo.AddComponent<Outline>();
            ol.effectColor = UIFontConfig.Colors.ParchmentOutline;
            ol.effectDistance = new Vector2(2.5f, -2.5f);
            panelGo.AddComponent<DragMove>().Target = panelRt;

            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(panelGo.transform, false);
            var titleRt = titleGo.AddComponent<RectTransform>();
            titleRt.anchorMin = new Vector2(0f, 1f); titleRt.anchorMax = new Vector2(1f, 1f); titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.sizeDelta = new Vector2(-20f, 38f); titleRt.anchoredPosition = new Vector2(0f, -10f);
            var title = titleGo.AddComponent<TextMeshProUGUI>();
            title.text = "Save Section"; title.fontSize = 24f; title.alignment = TextAlignmentOptions.Center;
            title.color = UIFontConfig.Colors.ParchmentInk;

            _nameField = UIBuilderHelper.CreateInputField(panelGo.transform, "NameField", "section name...",
                new Vector2(0.06f, 0.66f), new Vector2(0.94f, 0.80f), 16);
            _typeField = UIBuilderHelper.CreateInputField(panelGo.transform, "TypeField", "dungeon type (optional)...",
                new Vector2(0.06f, 0.47f), new Vector2(0.94f, 0.61f), 16);
            _notesField = UIBuilderHelper.CreateInputField(panelGo.transform, "NotesField", "notes / tags (optional)...",
                new Vector2(0.06f, 0.28f), new Vector2(0.94f, 0.42f), 16);

            if (!string.IsNullOrEmpty(_target?.DungeonType)) _typeField.text = _target.DungeonType;
            _nameField.onSubmit.AddListener(_ => Confirm());

            UIBuilderHelper.CreateParchmentButton(panelGo.transform, "Save",
                new Vector2(0.52f, 0.05f), new Vector2(0.94f, 0.22f), Confirm);
            UIBuilderHelper.CreateParchmentButton(panelGo.transform, "Cancel",
                new Vector2(0.06f, 0.05f), new Vector2(0.48f, 0.22f), Close);

            var averia = UIFontConfig.GetFont(UIFontConfig.FontStyle.AveriaLibre);
            if (averia != null)
                foreach (var tmp in _canvasGo.GetComponentsInChildren<TMP_Text>(true)) tmp.font = averia;

            _nameField.ActivateInputField();
            _nameField.Select();
        }

        private static void Confirm()
        {
            var t = _target;
            if (t == null) { Close(); return; }

            // Commas/semicolons break the .blueprint header round-trip — swap them out of the name + type.
            string name = (_nameField?.text ?? "").Trim().Replace(",", " ").Replace(";", " ");
            string type = (_typeField?.text ?? "").Trim().Replace(",", " ").Replace(";", " ");
            string notes = (_notesField?.text ?? "").Trim();

            if (!string.IsNullOrEmpty(type) && type != t.DungeonType) t.SetDungeonType(type);

            Close();
            t.SaveBlueprint(string.IsNullOrEmpty(name) ? null : name, string.IsNullOrEmpty(notes) ? null : notes);
            NotebookEditorPanel.Hide();   // done — close the notebook back to the world
        }

        public static void Close()
        {
            _target = null; _nameField = _typeField = _notesField = null;
            if (_canvasGo != null) { Object.Destroy(_canvasGo); _canvasGo = null; }
        }
    }
}
