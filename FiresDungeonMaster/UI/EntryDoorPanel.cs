using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using TMPro;
using HarmonyLib;
using FiresCore.UI;
using FiresCore.UI.ContextMenu;
using FiresDungeonMaster.Editor;

namespace FiresDungeonMaster.UI
{
    /// <summary>
    /// EntryDoorPanel — the admin config window for a placed entry door (opened by EntryDoorContextProvider's Shift+Alt
    /// right-click, or [E]). A standalone top-level ScreenSpaceOverlay parchment window (per reference_standalone_canvas)
    /// with cycle-rows + input fields that read/write the door's ZDO config live: DUNGEON (vanilla or a custom section
    /// set), MODE (full dungeon / empty room), ENVIRONMENT, LOCK (open / key / quest), DISPLAY NAME. Generate hands off
    /// to EntryDoorController.Generate (server-authoritative). Modality mirrors NotebookEditorPanel: InputBlock + freed
    /// cursor + SuppressDriver, Esc closes. Client-only.
    /// </summary>
    public static class EntryDoorPanel
    {
        private static GameObject _canvasGo, _panel;
        private static RectTransform _content;
        private static EntryDoorController _door;
        private static bool _blocked;
        private static List<KeyValuePair<string, bool>> _dungeonChoices; // label -> isVanilla
        private static TMP_InputField _nameField;   // the Display Name field — committed on Generate

        private static bool IsClientOnly() => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
        public static bool IsOpen => _canvasGo != null;

        public static void Open(EntryDoorController door)
        {
            if (IsClientOnly() || door == null) return;
            Close();
            _door = door;
            BuildDungeonChoices();
            Build();
        }

        private static void BuildDungeonChoices()
        {
            _dungeonChoices = new List<KeyValuePair<string, bool>>();
            foreach (var v in EntryDoorController.VanillaDungeons)
                _dungeonChoices.Add(new KeyValuePair<string, bool>(v, true));
            try
            {
                var lib = FiresDungeonMaster.Sections.FdmPaths.OpenLibrary();
                foreach (var t in FiresDungeonMaster.Generation.DungeonGenerationCommands.DistinctTypes(lib))
                    if (!string.IsNullOrEmpty(t)) _dungeonChoices.Add(new KeyValuePair<string, bool>(t, false));
            }
            catch { }
        }

        private static void Build()
        {
            _canvasGo = new GameObject("FDM_EntryDoorPanel");
            Object.DontDestroyOnLoad(_canvasGo);
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
            prt.sizeDelta = new Vector2(580f, 560f);
            prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.anchoredPosition = Vector2.zero;
            _panel.AddComponent<Image>().color = UIFontConfig.Colors.ParchmentPanel;
            var ol = _panel.AddComponent<Outline>();
            ol.effectColor = UIFontConfig.Colors.ParchmentOutline;
            ol.effectDistance = new Vector2(2.5f, -2.5f);
            _panel.AddComponent<DragMove>().Target = prt;

            var title = MakeLabel(_panel.transform, "Title", "Configure Entry Door", 24f, UIFontConfig.Colors.ParchmentInk, TextAlignmentOptions.Center);
            var trt = title.rectTransform;
            trt.anchorMin = new Vector2(0f, 1f); trt.anchorMax = new Vector2(1f, 1f); trt.pivot = new Vector2(0.5f, 1f);
            trt.sizeDelta = new Vector2(-24f, 40f); trt.anchoredPosition = new Vector2(0f, -12f);

            // Scrollless content area between the title and the footer buttons.
            var contentGo = new GameObject("Content", typeof(RectTransform));
            _content = contentGo.GetComponent<RectTransform>();
            _content.SetParent(_panel.transform, false);
            _content.anchorMin = new Vector2(0f, 0f); _content.anchorMax = new Vector2(1f, 1f);
            _content.offsetMin = new Vector2(0f, 64f); _content.offsetMax = new Vector2(0f, -60f);

            UIBuilderHelper.CreateParchmentButton(_panel.transform, "Generate",
                new Vector2(0.53f, 0.03f), new Vector2(0.95f, 0.10f), OnGenerate);
            UIBuilderHelper.CreateParchmentButton(_panel.transform, "Close",
                new Vector2(0.05f, 0.03f), new Vector2(0.47f, 0.10f), Close);

            var averia = UIFontConfig.GetFont(UIFontConfig.FontStyle.AveriaLibre);
            if (averia != null)
                foreach (var tmp in _canvasGo.GetComponentsInChildren<TMP_Text>(true)) tmp.font = averia;

            FiresContextMenu.SuppressDriver = true;
            // We open FROM the context-menu driver, which already acquired InputBlock and stands down (SuppressDriver),
            // handing us the release. So ALWAYS own it (_blocked = true OUTSIDE the guard) — otherwise Close() never
            // releases and movement stays frozen (and InputBlock is static, so the freeze survives logout).
            if (!InputBlock.IsBlocked) InputBlock.Block(true);
            _blocked = true;
            FiresCore.Input.FiresInputBlock.Acquire(PanelToken);   // suppress vanilla + mod hotkeys for the whole window
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
                Object.Destroy(c.gameObject);
            }

            int row = 0;

            // Dungeon — scrolling dropdown of vanilla dungeons + saved custom types.
            var dOpts = new List<string>();
            foreach (var c in _dungeonChoices) dOpts.Add(c.Key + (c.Value ? "  (vanilla)" : "  (custom)"));
            int dIdx = _dungeonChoices.FindIndex(c => string.Equals(c.Key, _door.DungeonType, System.StringComparison.OrdinalIgnoreCase));
            if (dIdx < 0) dIdx = 0;
            AddDropdownRow(row++, "Dungeon", dOpts, dIdx, i =>
            {
                if (i >= 0 && i < _dungeonChoices.Count) { _door.DungeonType = _dungeonChoices[i].Key; _door.IsVanilla = _dungeonChoices[i].Value; }
            });

            AddCycle(row++, "Mode", _door.EmptyBox ? "Empty Room" : "Full Dungeon", () => { _door.EmptyBox = !_door.EmptyBox; Rebuild(); });

            // Environment — scrolling dropdown of EVERY vanilla environment (the full EnvMan list), not just a handful.
            // EnvironmentNames[0] is "" (no override) — shown as "Nothing"; the real "" is stored on the door.
            var envNames = FiresCore.Utilities.EnvironmentBoxController.EnvironmentNames;
            var eOpts = new List<string>(envNames.Length);
            foreach (var e in envNames) eOpts.Add(string.IsNullOrEmpty(e) ? "Nothing" : e);
            int eIdx = System.Array.IndexOf(envNames, _door.Environment);
            if (eIdx < 0) eIdx = System.Array.IndexOf(envNames, "Crypt");
            if (eIdx < 0) eIdx = 0;
            AddDropdownRow(row++, "Environment", eOpts, eIdx, i =>
            {
                if (i >= 0 && i < envNames.Length) _door.Environment = envNames[i];
            });

            AddCycle(row++, "Lock", _door.Lock.ToString(), CycleLock);

            if (_door.Lock == EntryDoorController.DoorLockMode.Key)
            {
                AddCycle(row++, "Key", _door.KeyPrefab, () => { _door.KeyPrefab = CycleArray(EntryDoorController.KeyOptions, _door.KeyPrefab, +1); Rebuild(); });
                AddCycle(row++, "Pick Difficulty", _door.LockDifficulty, () => { _door.LockDifficulty = CycleArray(EntryDoorController.LockDifficulties, _door.LockDifficulty, +1); Rebuild(); });
            }
            else if (_door.Lock == EntryDoorController.DoorLockMode.Quest)
                AddInput(row++, "Quest ID", _door.QuestId, "rpgmaker quest id…", v => _door.QuestId = v);

            _nameField = AddInput(row++, "Display Name", _door.DisplayName, "shown on discovery…", v => _door.DisplayName = v);

            // Rows are added AFTER Build()'s font pass, so re-apply Averia to every new row label/button here.
            var averia = UIFontConfig.GetFont(UIFontConfig.FontStyle.AveriaLibre);
            if (averia != null)
                foreach (var tmp in _content.GetComponentsInChildren<TMP_Text>(true)) tmp.font = averia;

            // FDT context-menu look (dark rounded shell, gold text) — restyles the parchment build
            // in place; runs per rebuild since rows are recreated.
            FiresCore.UI.FiresPopupTheme.Reskin(_panel);
        }

        private static void CycleLock()
        {
            _door.Lock = (EntryDoorController.DoorLockMode)(((int)_door.Lock + 1) % 3);
            Rebuild();
        }

        private static string CycleArray(string[] arr, string current, int dir)
        {
            if (arr == null || arr.Length == 0) return current;
            int idx = System.Array.IndexOf(arr, current);
            if (idx < 0) idx = 0;
            idx = (idx + dir % arr.Length + arr.Length) % arr.Length;
            return arr[idx];
        }

        private const float RowH = 40f, RowGap = 6f;

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

        private static void AddCycle(int index, string label, string value, UnityEngine.Events.UnityAction onClick)
        {
            var row = NewRow(index);
            UIBuilderHelper.CreateParchmentButton(row, $"{label}:   {value}",
                new Vector2(0f, 0f), new Vector2(1f, 1f), onClick);
        }

        private static TMP_InputField AddInput(int index, string label, string value, string placeholder, System.Action<string> onChange)
        {
            var row = NewRow(index);
            var lbl = MakeLabel(row, "Label", label, 15f, UIFontConfig.Colors.ParchmentInk, TextAlignmentOptions.MidlineLeft);
            var lrt = lbl.rectTransform;
            lrt.anchorMin = new Vector2(0f, 0f); lrt.anchorMax = new Vector2(0.34f, 1f); lrt.offsetMin = lrt.offsetMax = Vector2.zero;

            var field = UIBuilderHelper.CreateInputField(row, "Field", placeholder,
                new Vector2(0.36f, 0.05f), new Vector2(1f, 0.95f), 15);
            if (!string.IsNullOrEmpty(value)) field.text = value;
            // Commit on both value-change and end-edit so the ZDO has the latest even if focus never left the field.
            field.onValueChanged.AddListener(v => onChange((v ?? "").Trim()));
            field.onEndEdit.AddListener(v => onChange((v ?? "").Trim()));
            return field;
        }

        // A label + a parchment-styled scrolling TMP_Dropdown. onChange fires with the picked index; it does NOT
        // Rebuild (Dungeon/Environment don't reveal other rows), so the open popup is never destroyed mid-callback.
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

            // CANONICAL uGUI dropdown template — NO layout group / fitter on Content. TMP_Dropdown positions and
            // sizes the item clones ITSELF against this exact structure (Content = one item high; items anchored to
            // the vertical middle). The old VerticalLayoutGroup fought that placement and swallowed the LAST option.
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
            cRt.anchoredPosition = Vector2.zero; cRt.sizeDelta = new Vector2(0, 28);   // ONE item high — the dropdown grows it per option

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

        private static void OnGenerate()
        {
            if (_door == null) return;
            // Commit the preset name from the field first (in case focus never left it), THEN generate. Generate is the
            // final step — it's effectively "Save", so close the window after.
            if (_nameField != null) _door.DisplayName = (_nameField.text ?? "").Trim();
            string r = _door.Generate();
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "[Dungeon] " + r);
            Close();
        }

        private static readonly object PanelToken = new object();

        public static void Close()
        {
            _door = null; _content = null; _panel = null;
            FiresContextMenu.SuppressDriver = false;
            FiresCore.Input.FiresInputBlock.Release(PanelToken);
            if (_blocked) { InputBlock.Block(false); _blocked = false; }
            if (_canvasGo != null) { Object.Destroy(_canvasGo); _canvasGo = null; }
        }

        // Keep the cursor freed while open and close on Esc (mirrors NotebookEditorPanel). Client-only patch.
        [HarmonyPatch(typeof(Hud), "Update")]
        internal static class Hud_Update_Patch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!IsOpen) return;
                if (Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
                if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
                if (!Cursor.visible) Cursor.visible = true;
            }
        }

        // SAFETY NET: on logout/world-unload, force-close every Fires modal and release BOTH input gates. InputBlock /
        // FiresInputBlock are static, so a leak from ANY panel would otherwise freeze movement across relogin (the
        // exact bug this whole class caused). Belt-and-suspenders — even after the _blocked fix above.
        [HarmonyPatch(typeof(ZNet), "Shutdown")]
        internal static class ZNet_Shutdown_PanelGuard
        {
            private static void Postfix()
            {
                if (IsClientOnly()) return;
                try { Close(); } catch { }
                try { NotebookEditorPanel.Hide(); } catch { }
                try { BlueprintBenchPanel.Hide(); } catch { }
                try { SaveSectionInput.Close(); } catch { }
                try { DungeonTypeInput.Close(); } catch { }
                try { FiresCore.Input.FiresInputBlock.ReleaseAll(); } catch { }
                try { if (FiresCore.UI.InputBlock.IsBlocked) FiresCore.UI.InputBlock.Block(false); } catch { }
            }
        }
    }
}
