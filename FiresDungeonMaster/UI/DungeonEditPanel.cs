using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using FiresCore.UI;
using FiresCore.UI.ContextMenu;
using FiresCore.Utilities;
using FiresDungeonMaster.Editor;

namespace FiresDungeonMaster.UI
{
    /// <summary>
    /// DungeonEditPanel — the POST-PLACEMENT dungeon editor, opened by Shift+Alt right-clicking the interior EXIT
    /// arch (EntryDoorContextProvider resolves the owning entry door + the dungeon's paired env box). One parchment
    /// window with BOTH halves of the customization:
    ///   • DUNGEON — display name (shows on the entry banner), environment (written to the door ZDO for regeneration
    ///     AND applied live to the paired box), lock mode / key / pick difficulty;
    ///   • ENV BOX — skybox, visibility, wind, time (auto-populated dropdowns), size fields + 0–1000% scale slider,
    ///     all applied live through the box's Set* methods.
    /// No Generate button — the dungeon already exists; every change applies immediately. Modality mirrors
    /// EntryDoorPanel (InputBlock + freed cursor + SuppressDriver, Esc closes). Client-only.
    /// </summary>
    public static class DungeonEditPanel
    {
        private static GameObject _canvasGo, _panel;
        private static RectTransform _content;
        private static EntryDoorController _door;
        private static EnvironmentBoxController _box;
        private static bool _blocked;
        private static TMP_InputField _sizeX, _sizeY, _sizeZ;

        private static bool IsClientOnly() => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
        public static bool IsOpen => _canvasGo != null;

        public static void Open(EntryDoorController door, EnvironmentBoxController box)
        {
            if (IsClientOnly() || door == null) return;
            Close();
            _door = door;
            _box = box;   // null-tolerant: dungeon rows still work; box rows show "(box not found)"
            Build();
        }

        private static void Build()
        {
            _canvasGo = new GameObject("FDM_DungeonEditPanel");
            UnityEngine.Object.DontDestroyOnLoad(_canvasGo);
            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 6400;
            var scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _canvasGo.AddComponent<GraphicRaycaster>();

            _panel = new GameObject("Panel");
            _panel.transform.SetParent(_canvasGo.transform, false);
            var prt = _panel.AddComponent<RectTransform>();
            prt.sizeDelta = new Vector2(660f, 830f);
            prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.anchoredPosition = Vector2.zero;
            _panel.AddComponent<Image>().color = UIFontConfig.Colors.ParchmentPanel;
            var ol = _panel.AddComponent<Outline>();
            ol.effectColor = UIFontConfig.Colors.ParchmentOutline;
            ol.effectDistance = new Vector2(2.5f, -2.5f);
            _panel.AddComponent<DragMove>().Target = prt;

            var title = MakeLabel(_panel.transform, "Title", "Edit Dungeon", 24f, UIFontConfig.Colors.ParchmentInk, TextAlignmentOptions.Center);
            var trt = title.rectTransform;
            trt.anchorMin = new Vector2(0f, 1f); trt.anchorMax = new Vector2(1f, 1f); trt.pivot = new Vector2(0.5f, 1f);
            trt.sizeDelta = new Vector2(-24f, 40f); trt.anchoredPosition = new Vector2(0f, -12f);

            var contentGo = new GameObject("Content", typeof(RectTransform));
            _content = contentGo.GetComponent<RectTransform>();
            _content.SetParent(_panel.transform, false);
            _content.anchorMin = new Vector2(0f, 0f); _content.anchorMax = new Vector2(1f, 1f);
            _content.offsetMin = new Vector2(0f, 64f); _content.offsetMax = new Vector2(0f, -56f);

            UIBuilderHelper.CreateParchmentButton(_panel.transform, "Close",
                new Vector2(0.30f, 0.02f), new Vector2(0.70f, 0.07f), Close);

            FiresContextMenu.SuppressDriver = true;
            if (!InputBlock.IsBlocked) InputBlock.Block(true);
            _blocked = true;
            InputBlockOwned.Acquire(PanelToken, _canvasGo);
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;

            Rebuild();
        }

        private static void Rebuild()
        {
            if (_content == null || _door == null) return;
            for (int i = _content.childCount - 1; i >= 0; i--)
            {
                var c = _content.GetChild(i);
                c.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(c.gameObject);
            }

            int row = 0;

            AddSection(row++, $"DUNGEON — {(_door.DungeonType == "" ? "(no type)" : _door.DungeonType)}");

            AddInput(row++, "Display Name", _door.DisplayName, "shown on entry…", v => _door.DisplayName = v);

            // Environment: persisted on the door (used on any regeneration) AND applied LIVE to the paired box.
            var envNames = EnvironmentBoxController.EnvironmentNames;
            var eOpts = new List<string>(envNames.Length);
            foreach (var e in envNames) eOpts.Add(string.IsNullOrEmpty(e) ? "Nothing" : e);
            int eIdx = Array.IndexOf(envNames, _door.Environment);
            if (eIdx < 0) eIdx = 0;
            AddDropdownRow(row++, "Environment", eOpts, eIdx, i =>
            {
                if (i < 0 || i >= envNames.Length) return;
                _door.Environment = envNames[i];
                _box?.SetEnvironment(envNames[i]);
            });

            AddCycle(row++, "Lock", _door.Lock.ToString(), () =>
            {
                _door.Lock = (EntryDoorController.DoorLockMode)(((int)_door.Lock + 1) % 3);
                Rebuild();
            });
            if (_door.Lock == EntryDoorController.DoorLockMode.Key)
            {
                AddCycle(row++, "Key", _door.KeyPrefab, () => { _door.KeyPrefab = CycleArray(EntryDoorController.KeyOptions, _door.KeyPrefab, +1); Rebuild(); });
                AddCycle(row++, "Pick Difficulty", _door.LockDifficulty, () => { _door.LockDifficulty = CycleArray(EntryDoorController.LockDifficulties, _door.LockDifficulty, +1); Rebuild(); });
            }
            else if (_door.Lock == EntryDoorController.DoorLockMode.Quest)
                AddInput(row++, "Quest ID", _door.QuestId, "rpgmaker quest id…", v => _door.QuestId = v);

            AddSection(row++, _box != null ? "ENVIRONMENT BOX" : "ENVIRONMENT BOX — (box not found)");

            if (_box != null)
            {
                var skyboxes = (EnvironmentBoxController.SkyboxMode[])Enum.GetValues(typeof(EnvironmentBoxController.SkyboxMode));
                var sOpts = new List<string>(skyboxes.Length);
                foreach (var s in skyboxes) sOpts.Add(s.ToString());
                AddDropdownRow(row++, "Skybox", sOpts, Array.IndexOf(skyboxes, _box.CurrentSkyboxMode), i =>
                {
                    if (i >= 0 && i < skyboxes.Length) _box.SetSkyboxMode(skyboxes[i]);
                });

                var visibilities = new[]
                {
                    EnvironmentBoxController.VisibilityMode.Full,
                    EnvironmentBoxController.VisibilityMode.EdgesOnly,
                    EnvironmentBoxController.VisibilityMode.Hidden,
                };
                var vOpts = new List<string> { "Full", "EdgesOnly", "Hidden" };
                int vIdx = Array.IndexOf(visibilities, _box.CurrentVisibility);
                if (vIdx < 0) vIdx = 0;
                AddDropdownRow(row++, "Visibility", vOpts, vIdx, i =>
                {
                    if (i >= 0 && i < visibilities.Length) _box.SetVisibility(visibilities[i]);
                });

                var winds = (EnvironmentBoxController.WindIntensity[])Enum.GetValues(typeof(EnvironmentBoxController.WindIntensity));
                var wOpts = new List<string>(winds.Length);
                foreach (var w in winds) wOpts.Add(EnvironmentBoxController.GetWindIntensityName(w));
                AddDropdownRow(row++, "Wind", wOpts, Array.IndexOf(winds, _box.CurrentWindIntensity), i =>
                {
                    if (i >= 0 && i < winds.Length) _box.SetWindIntensity(winds[i]);
                });

                var times = (EnvironmentBoxController.ForcedTimeOfDay[])Enum.GetValues(typeof(EnvironmentBoxController.ForcedTimeOfDay));
                var tOpts = new List<string>(times.Length);
                foreach (var t in times) tOpts.Add(EnvironmentBoxController.GetTimeOfDayName(t));
                AddDropdownRow(row++, "Time", tOpts, Array.IndexOf(times, _box.CurrentTimeOfDay), i =>
                {
                    if (i >= 0 && i < times.Length) _box.SetTimeOfDay(times[i]);
                });

                AddSizeRow(row++);
                AddScaleRow(row++);
            }

            var averia = UIFontConfig.GetFont(UIFontConfig.FontStyle.AveriaLibre);
            if (averia != null)
                foreach (var tmp in _content.GetComponentsInChildren<TMP_Text>(true)) tmp.font = averia;
        }

        private static string CycleArray(string[] arr, string current, int dir)
        {
            if (arr == null || arr.Length == 0) return current;
            int idx = Array.IndexOf(arr, current);
            if (idx < 0) idx = 0;
            idx = (idx + dir % arr.Length + arr.Length) % arr.Length;
            return arr[idx];
        }

        private const float RowH = 46f, RowGap = 8f;

        private static RectTransform NewRow(int index)
        {
            var go = new GameObject("Row" + index, typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(_content, false);
            rt.anchorMin = new Vector2(0.05f, 1f); rt.anchorMax = new Vector2(0.95f, 1f); rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, RowH);
            rt.anchoredPosition = new Vector2(0f, -index * (RowH + RowGap));
            return rt;
        }

        private static void AddSection(int index, string label)
        {
            var row = NewRow(index);
            var t = MakeLabel(row, "Section", label, 17f, UIFontConfig.Colors.ParchmentInk, TextAlignmentOptions.BottomLeft);
            t.fontStyle = FontStyles.Bold;
            var rt = t.rectTransform; rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        private static void AddCycle(int index, string label, string value, UnityEngine.Events.UnityAction onClick)
        {
            var row = NewRow(index);
            UIBuilderHelper.CreateParchmentButton(row, $"{label}:   {value}",
                new Vector2(0f, 0f), new Vector2(1f, 1f), onClick);
        }

        private static TMP_InputField AddInput(int index, string label, string value, string placeholder, Action<string> onChange)
        {
            var row = NewRow(index);
            var lbl = MakeLabel(row, "Label", label, 15f, UIFontConfig.Colors.ParchmentInk, TextAlignmentOptions.MidlineLeft);
            var lrt = lbl.rectTransform;
            lrt.anchorMin = new Vector2(0f, 0f); lrt.anchorMax = new Vector2(0.34f, 1f); lrt.offsetMin = lrt.offsetMax = Vector2.zero;

            var field = UIBuilderHelper.CreateInputField(row, "Field", placeholder,
                new Vector2(0.36f, 0.05f), new Vector2(1f, 0.95f), 15);
            if (!string.IsNullOrEmpty(value)) field.text = value;
            field.onValueChanged.AddListener(v => onChange((v ?? "").Trim()));
            field.onEndEdit.AddListener(v => onChange((v ?? "").Trim()));
            return field;
        }

        // Size X/Y/Z fields — live-applied to the paired box (SetSize clamps + persists), clamped values echo back.
        private static void AddSizeRow(int index)
        {
            var row = NewRow(index);
            var lbl = MakeLabel(row, "Label", "Size (X·Y·Z)", 15f, UIFontConfig.Colors.ParchmentInk, TextAlignmentOptions.MidlineLeft);
            var lrt = lbl.rectTransform;
            lrt.anchorMin = new Vector2(0f, 0f); lrt.anchorMax = new Vector2(0.34f, 1f); lrt.offsetMin = lrt.offsetMax = Vector2.zero;

            _sizeX = SizeField(row, "X", new Vector2(0.36f, 0.05f), new Vector2(0.56f, 0.95f), _box.BoxSize.x);
            _sizeY = SizeField(row, "Y", new Vector2(0.58f, 0.05f), new Vector2(0.78f, 0.95f), _box.BoxSize.y);
            _sizeZ = SizeField(row, "Z", new Vector2(0.80f, 0.05f), new Vector2(1f, 0.95f), _box.BoxSize.z);
        }

        private static TMP_InputField SizeField(RectTransform row, string name, Vector2 aMin, Vector2 aMax, float value)
        {
            var field = UIBuilderHelper.CreateInputField(row, name, name, aMin, aMax, 15);
            field.contentType = TMP_InputField.ContentType.DecimalNumber;
            field.text = value.ToString("0.#");
            field.onEndEdit.AddListener(_ => ApplySize());
            return field;
        }

        private static void ApplySize()
        {
            if (_box == null) return;
            Vector3 s = _box.BoxSize;
            if (float.TryParse(_sizeX != null ? _sizeX.text : "", out float x)) s.x = x;
            if (float.TryParse(_sizeY != null ? _sizeY.text : "", out float y)) s.y = y;
            if (float.TryParse(_sizeZ != null ? _sizeZ.text : "", out float z)) s.z = z;
            _box.SetSize(s);
            RefreshSizeFields();
        }

        private static void RefreshSizeFields()
        {
            if (_box == null) return;
            if (_sizeX != null) _sizeX.SetTextWithoutNotify(_box.BoxSize.x.ToString("0.#"));
            if (_sizeY != null) _sizeY.SetTextWithoutNotify(_box.BoxSize.y.ToString("0.#"));
            if (_sizeZ != null) _sizeZ.SetTextWithoutNotify(_box.BoxSize.z.ToString("0.#"));
        }

        // 100% = the vanilla interior env box (64 x 500 x 64). Live scaling as the handle drags.
        private static readonly Vector3 ScaleBase = new Vector3(64f, 500f, 64f);

        private static void AddScaleRow(int index)
        {
            var row = NewRow(index);
            float pct = Mathf.Clamp(_box.BoxSize.x / ScaleBase.x * 100f, 0f, 1000f);
            var lbl = MakeLabel(row, "Label", $"Scale: {pct:0}%", 15f, UIFontConfig.Colors.ParchmentInk, TextAlignmentOptions.MidlineLeft);
            var lrt = lbl.rectTransform;
            lrt.anchorMin = new Vector2(0f, 0f); lrt.anchorMax = new Vector2(0.34f, 1f); lrt.offsetMin = lrt.offsetMax = Vector2.zero;

            var sgo = new GameObject("ScaleSlider", typeof(RectTransform));
            sgo.transform.SetParent(row, false);
            var srt = (RectTransform)sgo.transform;
            srt.anchorMin = new Vector2(0.36f, 0.30f); srt.anchorMax = new Vector2(1f, 0.70f); srt.offsetMin = srt.offsetMax = Vector2.zero;
            sgo.AddComponent<Image>().color = UIFontConfig.Colors.ParchmentField;

            var slider = sgo.AddComponent<Slider>();

            var fillArea = new GameObject("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(sgo.transform, false);
            var faRt = (RectTransform)fillArea.transform;
            faRt.anchorMin = Vector2.zero; faRt.anchorMax = Vector2.one; faRt.offsetMin = new Vector2(2f, 2f); faRt.offsetMax = new Vector2(-2f, -2f);
            var fillGo = new GameObject("Fill", typeof(RectTransform));
            fillGo.transform.SetParent(fillArea.transform, false);
            var fillRt = (RectTransform)fillGo.transform;
            fillRt.anchorMin = Vector2.zero; fillRt.anchorMax = Vector2.one; fillRt.offsetMin = fillRt.offsetMax = Vector2.zero;
            fillGo.AddComponent<Image>().color = UIFontConfig.Colors.ParchmentButton;

            var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
            handleArea.transform.SetParent(sgo.transform, false);
            var haRt = (RectTransform)handleArea.transform;
            haRt.anchorMin = Vector2.zero; haRt.anchorMax = Vector2.one; haRt.offsetMin = new Vector2(8f, 0f); haRt.offsetMax = new Vector2(-8f, 0f);
            var handleGo = new GameObject("Handle", typeof(RectTransform));
            handleGo.transform.SetParent(handleArea.transform, false);
            var hRt = (RectTransform)handleGo.transform;
            hRt.sizeDelta = new Vector2(16f, 0f);
            var hImg = handleGo.AddComponent<Image>(); hImg.color = UIFontConfig.Colors.ParchmentButtonInk;

            slider.fillRect = fillRt;
            slider.handleRect = hRt;
            slider.targetGraphic = hImg;
            slider.minValue = 0f; slider.maxValue = 1000f; slider.wholeNumbers = true;
            slider.SetValueWithoutNotify(pct);
            slider.onValueChanged.AddListener(v =>
            {
                if (_box == null) return;
                _box.SetSize(ScaleBase * (v / 100f));
                RefreshSizeFields();
                float applied = Mathf.Clamp(_box.BoxSize.x / ScaleBase.x * 100f, 0f, 1000f);
                lbl.text = $"Scale: {applied:0}%";
            });
        }

        private static void AddDropdownRow(int index, string label, List<string> options, int value, UnityEngine.Events.UnityAction<int> onChange)
        {
            var row = NewRow(index);
            var lbl = MakeLabel(row, "Label", label, 15f, UIFontConfig.Colors.ParchmentInk, TextAlignmentOptions.MidlineLeft);
            var lrt = lbl.rectTransform;
            lrt.anchorMin = new Vector2(0f, 0f); lrt.anchorMax = new Vector2(0.34f, 1f); lrt.offsetMin = lrt.offsetMax = Vector2.zero;

            var holder = new GameObject("DDHolder", typeof(RectTransform));
            var hrt = holder.GetComponent<RectTransform>();
            hrt.SetParent(row, false);
            hrt.anchorMin = new Vector2(0.36f, 0.08f); hrt.anchorMax = new Vector2(1f, 0.92f); hrt.offsetMin = hrt.offsetMax = Vector2.zero;
            MakeDropdown(holder.transform, options, value, onChange);
        }

        // Parchment scrolling TMP_Dropdown on the CANONICAL uGUI template (no layout group on Content — the
        // dropdown positions its item clones itself; a layout group swallows the last option).
        private static TMP_Dropdown MakeDropdown(Transform parent, List<string> options, int value, UnityEngine.Events.UnityAction<int> onChange)
        {
            var go = new GameObject("Dropdown", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
            var bg = go.AddComponent<Image>(); bg.color = UIFontConfig.Colors.ParchmentField; bg.raycastTarget = true;
            var dd = go.AddComponent<TMP_Dropdown>();

            var cap = ChildText(go.transform, "Label", 14f, UIFontConfig.Colors.ParchmentInk, TextAlignmentOptions.MidlineLeft);
            var capRt = cap.rectTransform; capRt.anchorMin = Vector2.zero; capRt.anchorMax = Vector2.one; capRt.offsetMin = new Vector2(8, 2); capRt.offsetMax = new Vector2(-20, -2);

            var arr = ChildText(go.transform, "Arrow", 10f, UIFontConfig.Colors.ParchmentInk, TextAlignmentOptions.Center);
            arr.text = "▼";
            var arrRt = arr.rectTransform; arrRt.anchorMin = new Vector2(1, 0.5f); arrRt.anchorMax = new Vector2(1, 0.5f); arrRt.pivot = new Vector2(1, 0.5f); arrRt.anchoredPosition = new Vector2(-6, 0); arrRt.sizeDelta = new Vector2(16, 16);

            var tmpl = new GameObject("Template", typeof(RectTransform)); tmpl.transform.SetParent(go.transform, false);
            var tRt = tmpl.GetComponent<RectTransform>();
            tRt.anchorMin = new Vector2(0, 0); tRt.anchorMax = new Vector2(1, 0); tRt.pivot = new Vector2(0.5f, 1); tRt.anchoredPosition = new Vector2(0, 2); tRt.sizeDelta = new Vector2(0, 168);
            tmpl.AddComponent<Image>().color = UIFontConfig.Colors.ParchmentPanel;
            var tScroll = tmpl.AddComponent<ScrollRect>();

            var vp = new GameObject("Viewport", typeof(RectTransform)); vp.transform.SetParent(tmpl.transform, false);
            var vpRt = vp.GetComponent<RectTransform>();
            vpRt.anchorMin = Vector2.zero; vpRt.anchorMax = Vector2.one; vpRt.pivot = new Vector2(0f, 1f);
            vpRt.offsetMin = new Vector2(2, 2); vpRt.offsetMax = new Vector2(-2, -2);
            vp.AddComponent<RectMask2D>();

            var content = new GameObject("Content", typeof(RectTransform)); content.transform.SetParent(vp.transform, false);
            var cRt = content.GetComponent<RectTransform>();
            cRt.anchorMin = new Vector2(0f, 1f); cRt.anchorMax = new Vector2(1f, 1f); cRt.pivot = new Vector2(0.5f, 1f);
            cRt.anchoredPosition = Vector2.zero; cRt.sizeDelta = new Vector2(0, 28);

            tScroll.viewport = vpRt; tScroll.content = cRt; tScroll.horizontal = false; tScroll.vertical = true; tScroll.movementType = ScrollRect.MovementType.Clamped; tScroll.scrollSensitivity = 24f;

            var item = new GameObject("Item", typeof(RectTransform), typeof(Toggle));
            item.transform.SetParent(content.transform, false);
            var iRt = (RectTransform)item.transform;
            iRt.anchorMin = new Vector2(0f, 0.5f); iRt.anchorMax = new Vector2(1f, 0.5f); iRt.sizeDelta = new Vector2(0, 28);

            var itemBgGo = new GameObject("Item Background", typeof(RectTransform), typeof(Image));
            itemBgGo.transform.SetParent(item.transform, false);
            var ibRt = (RectTransform)itemBgGo.transform; ibRt.anchorMin = Vector2.zero; ibRt.anchorMax = Vector2.one; ibRt.offsetMin = ibRt.offsetMax = Vector2.zero;
            var itemBg = itemBgGo.GetComponent<Image>(); itemBg.color = UIFontConfig.Colors.ParchmentButton;

            var checkGo = new GameObject("Item Checkmark", typeof(RectTransform), typeof(Image));
            checkGo.transform.SetParent(item.transform, false);
            var ckRt = (RectTransform)checkGo.transform;
            ckRt.anchorMin = new Vector2(0f, 0.5f); ckRt.anchorMax = new Vector2(0f, 0.5f); ckRt.pivot = new Vector2(0f, 0.5f);
            ckRt.anchoredPosition = new Vector2(6f, 0f); ckRt.sizeDelta = new Vector2(10f, 10f);
            var checkImg = checkGo.GetComponent<Image>(); checkImg.color = UIFontConfig.Colors.ParchmentButtonInk;

            var itemLbl = ChildText(item.transform, "Item Label", 14f, UIFontConfig.Colors.ParchmentButtonInk, TextAlignmentOptions.MidlineLeft);
            var ilRt = itemLbl.rectTransform; ilRt.anchorMin = Vector2.zero; ilRt.anchorMax = Vector2.one; ilRt.offsetMin = new Vector2(22, 0); ilRt.offsetMax = new Vector2(-6, 0);

            var toggle = item.GetComponent<Toggle>();
            toggle.targetGraphic = itemBg;
            toggle.graphic = checkImg;

            dd.template = tRt; dd.captionText = cap; dd.itemText = itemLbl;
            tmpl.SetActive(false);

            dd.ClearOptions();
            dd.AddOptions(options ?? new List<string>());
            dd.value = Mathf.Clamp(value, 0, Mathf.Max(0, (options?.Count ?? 1) - 1));
            dd.RefreshShownValue();
            dd.onValueChanged.AddListener(onChange);
            return dd;
        }

        private static TextMeshProUGUI ChildText(Transform parent, string name, float size, Color color, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.fontSize = size; t.color = color; t.alignment = align; t.raycastTarget = false; t.enableWordWrapping = false;
            return t;
        }

        private static TextMeshProUGUI MakeLabel(Transform parent, string name, string text, float size, Color color, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text; t.fontSize = size; t.color = color; t.alignment = align; t.raycastTarget = false;
            return t;
        }

        private static readonly object PanelToken = new object();

        public static void Close()
        {
            _door = null; _box = null; _content = null; _panel = null;
            _sizeX = _sizeY = _sizeZ = null;
            FiresContextMenu.SuppressDriver = false;
            FiresCore.Input.FiresInputBlock.Release(PanelToken);
            if (_blocked) { InputBlock.Block(false); _blocked = false; }
            if (_canvasGo != null) { UnityEngine.Object.Destroy(_canvasGo); _canvasGo = null; }
        }

        // Keep the cursor freed while open and close on Esc (mirrors EntryDoorPanel). Client-only patch.
        [HarmonyPatch(typeof(Hud), "Update")]
        internal static class Hud_Update_Patch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!IsOpen) return;
                if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
                if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
                if (!Cursor.visible) Cursor.visible = true;
            }
        }
    }
}
