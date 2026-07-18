using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using TMPro;
using FiresCore.UI;
using FiresDungeonMaster.Editor;

namespace FiresDungeonMaster.UI
{
    /// <summary>
    /// DungeonTypeInput — a tiny client-only modal for typing a brand-new dungeon-type name onto a footprint.
    /// Opened by SectionFootprintController.Interact (E on the center sphere). Built on a STANDALONE top-level
    /// ScreenSpaceOverlay canvas (per reference_standalone_canvas — never parented under the Hud canvas) and a
    /// FiresCore.UI.UIBuilderHelper.CreateInputField for the parchment text entry. Core's FiresInputBlockDriver
    /// auto-acquires the input gate when the TMP_InputField is focused, so no manual Acquire/Release wiring is
    /// needed and typing won't leak to vanilla/mod hotkeys.
    ///
    /// Dedi-safe: never instantiated headless (Interact early-returns on IsClientOnly, and Open() guards too).
    /// </summary>
    public static class DungeonTypeInput
    {
        private static GameObject _canvasGo;
        private static TMP_InputField _input;
        private static SectionFootprintController _target;

        private static bool IsClientOnly()
            => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

        public static void Open(SectionFootprintController target)
        {
            if (IsClientOnly() || target == null) return;
            Close(); // single instance
            _target = target;
            Build();
        }

        private static void Build()
        {
            _canvasGo = new GameObject("FDM_DungeonTypeInput");
            Object.DontDestroyOnLoad(_canvasGo);

            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 7500; // above the notebook (6300) so it's usable, never hidden behind it
            var scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            _canvasGo.AddComponent<GraphicRaycaster>();

            // Dim backdrop panel (centered ~520x180).
            var panelGo = new GameObject("Panel");
            panelGo.transform.SetParent(_canvasGo.transform, false);
            var panelRt = panelGo.AddComponent<RectTransform>();
            panelRt.sizeDelta = new Vector2(520f, 180f);
            panelRt.anchorMin = panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.anchoredPosition = Vector2.zero;
            var panelImg = panelGo.AddComponent<Image>();
            panelImg.color = UIFontConfig.Colors.ParchmentPanel;
            var panelOutline = panelGo.AddComponent<Outline>();
            panelOutline.effectColor = UIFontConfig.Colors.ParchmentOutline;
            panelOutline.effectDistance = new Vector2(2.5f, -2.5f);
            panelGo.AddComponent<DragMove>().Target = panelRt;   // draggable like the other Fires popups

            // Title.
            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(panelGo.transform, false);
            var titleRt = titleGo.AddComponent<RectTransform>();
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.sizeDelta = new Vector2(-20f, 36f);
            titleRt.anchoredPosition = new Vector2(0f, -10f);
            var title = titleGo.AddComponent<TextMeshProUGUI>();
            title.text = "New Dungeon Type";
            title.fontSize = 22f;
            title.alignment = TextAlignmentOptions.Center;
            title.color = UIFontConfig.Colors.ParchmentInk;

            // Parchment Fires input field (Core helper — builds Viewport/RectMask2D + Text + Placeholder).
            _input = UIBuilderHelper.CreateInputField(
                panelGo.transform, "DungeonTypeField", "dungeon-type name...",
                new Vector2(0.06f, 0.34f), new Vector2(0.94f, 0.58f), 16);
            if (!string.IsNullOrEmpty(_target?.DungeonType))
                _input.text = _target.DungeonType;
            _input.onSubmit.AddListener(_ => Confirm());

            // Set + Cancel buttons. CreateButton's real signature is
            // (Transform parent, string text, Vector2 anchorMin, Vector2 anchorMax, UnityAction onClick=null) —
            // the label is the 2nd arg and onClick is passed inline (NO separate name arg, NO trailing AddListener).
            UIBuilderHelper.CreateParchmentButton(panelGo.transform, "Set",
                new Vector2(0.52f, 0.06f), new Vector2(0.94f, 0.26f), Confirm);
            UIBuilderHelper.CreateParchmentButton(panelGo.transform, "Cancel",
                new Vector2(0.06f, 0.06f), new Vector2(0.48f, 0.26f), Close);

            var averia = UIFontConfig.GetFont(UIFontConfig.FontStyle.AveriaLibre);
            if (averia != null)
                foreach (var tmp in _canvasGo.GetComponentsInChildren<TMP_Text>(true)) tmp.font = averia;

            _input.ActivateInputField();
            _input.Select();
        }

        private static void Confirm()
        {
            if (_target != null && _input != null)
            {
                string name = (_input.text ?? "").Trim();
                // ExtraInfo/Data round-trip corrupts commas; keep dungeon-type names comma-free for #fdm_dungeontype too.
                name = name.Replace(",", " ").Replace(";", " ");
                _target.SetDungeonType(name);
            }
            Close();
            // The section editor panel is still open BEHIND this modal; refresh it so the new type shows in its
            // readout immediately (otherwise the write succeeds but the stale panel makes it look like nothing happened).
            SectionEditorPanel.RefreshIfOpen();
            NotebookEditorPanel.RefreshIfOpen();
        }

        public static void Close()
        {
            _target = null;
            _input = null;
            if (_canvasGo != null)
            {
                Object.Destroy(_canvasGo);
                _canvasGo = null;
            }
        }
    }
}
