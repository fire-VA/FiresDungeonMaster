using System;
using FiresCore.Logging;
using FiresCore.UI;
using FiresCore.UI.ContextMenu;
using FiresDungeonMaster.Editor;
using FiresDungeonMaster.Sections;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace FiresDungeonMaster.UI
{
    using MarkerHit = SectionFootprintController.MarkerHit;
    using EdgeTag = SectionFootprintController.EdgeTag;

    /// <summary>
    /// NotebookEditorPanel — runtime controller for the BAKED weathered field-notebook editor (prefab
    /// 'FiresNotebookEditor' in the firesdungeonmaster bundle, authored by FiresDungeonMasterBuilder.BuildNotebookEditor
    /// per Tools/BakedUIPipeline). This REPLACES the runtime parchment SectionEditorPanel as the window the footprint
    /// right-click claim opens: aim a footprint sphere, Alt+Shift right-click, and the claim
    /// (SectionFootprintContextProvider) hands off to this notebook instead of the shared companion menu.
    ///
    /// The prefab supplies the FRAME (aged page + leather cover + elastic strap, hand-written title/subtitle/prompt,
    /// the pencil checklist RowTemplate, close + resize + drag handles). The controller fills ContentArea at runtime:
    ///   - EDGE sphere  -> a pencil checklist of Wall / Doorway / Open / Window / Back (current choice = red-filled box);
    ///   - CENTER sphere-> a "what is this room?" role checklist + dungeon-type / height / save action lines.
    /// Every edit rebuilds the list so the live choice stays circled, and the footprint's blue guide walls rebuild.
    ///
    /// Modality mirrors the old SectionEditorPanel handshake (it is opened FROM the context driver): SuppressDriver so
    /// the driver stands down and hands us its InputBlock, cursor freed, crosshair raycast suppressed, Esc closes.
    /// Client-only (the whole thing no-ops headless).
    /// </summary>
    public static class NotebookEditorPanel
    {
        private const string PrefabName = "FiresNotebookEditor";

        private static GameObject _canvasGo, _panel;
        private static Transform _root;
        private static bool _initFailed, _blocked;

        private static RectTransform _rowParent;    // runtime scroll content
        private static GameObject _rowTemplate;
        private static TMP_Text _titleT, _subT, _promptT, _footerT;

        private static SectionFootprintController _fp;
        private static MarkerHit _marker;

        private static readonly Color NbRed = new Color(0.66f, 0.243f, 0.169f);
        private static readonly Color NbInk = new Color(0.20f, 0.15f, 0.06f);
        private static readonly Color NbPencil = new Color(0.29f, 0.235f, 0.157f);
        private static readonly Color NbNote = new Color(0.48f, 0.404f, 0.29f);
        private static readonly Color NbCream = new Color(0.93f, 0.88f, 0.73f, 1f);
        private static readonly Color NbCheckEmpty = new Color(0.85f, 0.78f, 0.60f, 1f);

        private static readonly EdgeTag[] EdgeChoices =
            { EdgeTag.Wall, EdgeTag.Doorway, EdgeTag.Open, EdgeTag.Window, EdgeTag.Back };

        private static bool IsClientOnly() => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
        public static bool IsOpen => _canvasGo != null && _canvasGo.activeSelf;

        /// <summary>Rebuild the open notebook's body so a change made by a separate modal (the dungeon-type text input)
        /// shows immediately. No-op when closed.</summary>
        public static void RefreshIfOpen() { if (IsOpen && _fp != null) Rebuild(); }

        // ── open / close ─────────────────────────────────────────────────────────────
        /// <summary>Open (or retarget) the notebook for one footprint sphere. A second sphere retargets in place.</summary>
        public static void Open(SectionFootprintController fp, MarkerHit marker)
        {
            if (IsClientOnly() || fp == null) return;
            if (!EnsureBuilt()) { SectionEditorPanel.Open(fp, marker); return; }   // fall back to the parchment window if the bundle is missing
            _fp = fp; _marker = marker;
            _canvasGo.SetActive(true);
            _canvasGo.transform.SetAsLastSibling();
            SortAboveMap();

            FiresContextMenu.SuppressDriver = true;               // driver stands down; we own its InputBlock now
            if (!InputBlock.IsBlocked) InputBlock.Block(true);
            _blocked = true;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            Rebuild();
        }

        public static void Hide()
        {
            if (_canvasGo != null) _canvasGo.SetActive(false);
            if (_blocked) { InputBlock.Block(false); _blocked = false; }
            FiresContextMenu.SuppressDriver = false;
            SetCrosshair(true);
            _fp = null;
        }

        // Hide/show Valheim's aiming crosshair reticle so it doesn't bleed through the open notebook as a grey cross.
        private static void SetCrosshair(bool show)
        {
            try { var h = Hud.instance; if (h != null && h.m_crosshair != null && h.m_crosshair.gameObject.activeSelf != show) h.m_crosshair.gameObject.SetActive(show); }
            catch { }
        }

        private static bool EnsureBuilt()
        {
            if (_panel != null) return true;
            if (_initFailed) return false;
            try
            {
                var prefab = FootprintRegistration.LoadUiPrefab(PrefabName);
                if (prefab == null) { FiresLogger.LogError($"'{PrefabName}' not in bundle — rebuild the firesdungeonmaster bundle."); _initFailed = true; return false; }

                _canvasGo = new GameObject("FDM_NotebookEditorCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                UnityEngine.Object.DontDestroyOnLoad(_canvasGo);
                var c = _canvasGo.GetComponent<Canvas>(); c.renderMode = RenderMode.ScreenSpaceOverlay; c.sortingOrder = 6300;
                var s = _canvasGo.GetComponent<CanvasScaler>(); s.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; s.referenceResolution = new Vector2(1920f, 1080f); s.matchWidthOrHeight = 0.5f;
                EnsureEventSystem();

                _panel = UnityEngine.Object.Instantiate(prefab, _canvasGo.transform);
                _root = _panel.transform;
                var rt = _panel.GetComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f); rt.anchoredPosition = Vector2.zero;

                Bind();
                FiresCore.Bridge.ModUiRegistry.Register("FDM_NotebookEditor", () => IsOpen, Hide, () => _panel);
                _canvasGo.SetActive(false);
                return true;
            }
            catch (Exception ex) { FiresLogger.LogError($"notebook editor init failed: {ex}"); _initFailed = true; return false; }
        }

        private static void Bind()
        {
            _titleT = Tmp("TitleText"); _subT = Tmp("SubtitleText"); _promptT = Tmp("PromptText"); _footerT = Tmp("FooterText");
            Click("CloseButton", () => { Sfx(); Hide(); });

            var header = Find("Header");
            if (header != null) { var d = header.GetComponent<DragMove>() ?? header.AddComponent<DragMove>(); d.Target = _panel.GetComponent<RectTransform>(); }
            var grip = Find("ResizeHandle");
            if (grip != null)
            {
                var img = grip.GetComponent<Image>(); if (img != null) img.raycastTarget = true;
                var r = grip.GetComponent<DragResize>() ?? grip.AddComponent<DragResize>(); r.Target = _panel.GetComponent<RectTransform>(); r.MinSize = new Vector2(520f, 560f);
            }

            _rowTemplate = Find("RowTemplate");
            _rowParent = BuildScroll();
        }

        // Wrap the baked ContentArea in a runtime ScrollRect + RectMask2D (ScrollPool pattern — the baked prefab
        // carries no stencil Mask). Moves RowTemplate under Content, destroys the baked sample rows.
        private static RectTransform BuildScroll()
        {
            var area = Find("ContentArea")?.transform;
            if (area == null) return null;
            foreach (var lg in area.GetComponents<VerticalLayoutGroup>()) { lg.enabled = false; UnityEngine.Object.Destroy(lg); }
            foreach (var f in area.GetComponents<ContentSizeFitter>()) { f.enabled = false; UnityEngine.Object.Destroy(f); }

            var scroll = new GameObject("NbScroll", typeof(RectTransform), typeof(ScrollRect));
            scroll.transform.SetParent(area, false);
            var srt = (RectTransform)scroll.transform; srt.anchorMin = Vector2.zero; srt.anchorMax = Vector2.one; srt.offsetMin = srt.offsetMax = Vector2.zero;
            var vp = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            vp.transform.SetParent(scroll.transform, false);
            var vprt = (RectTransform)vp.transform; vprt.anchorMin = Vector2.zero; vprt.anchorMax = Vector2.one; vprt.offsetMin = Vector2.zero; vprt.offsetMax = new Vector2(-13f, 0f);
            vp.GetComponent<Image>().color = new Color(0, 0, 0, 0);
            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(vp.transform, false);
            var crt = (RectTransform)content.transform;
            crt.anchorMin = new Vector2(0, 1); crt.anchorMax = new Vector2(1, 1); crt.pivot = new Vector2(0.5f, 1);
            crt.offsetMin = Vector2.zero; crt.offsetMax = Vector2.zero;   // width follows the viewport (else a code-built RectTransform stays zero-width)
            var vlg = content.AddComponent<VerticalLayoutGroup>(); vlg.childControlWidth = vlg.childForceExpandWidth = true; vlg.childControlHeight = true; vlg.childForceExpandHeight = false; vlg.spacing = 6f;
            content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var sr = scroll.GetComponent<ScrollRect>(); sr.viewport = vprt; sr.content = crt; sr.horizontal = false; sr.vertical = true; sr.movementType = ScrollRect.MovementType.Clamped; sr.scrollSensitivity = 28f;
            BlueprintBenchPanel.BuildScrollbar(scroll.transform, sr, new Color(0.35f, 0.27f, 0.16f, 0.35f), new Color(0.30f, 0.24f, 0.16f, 0.7f));

            if (_rowTemplate != null) { _rowTemplate.transform.SetParent(crt, false); _rowTemplate.SetActive(false); }
            for (int i = area.childCount - 1; i >= 0; i--)
            {
                var ch = area.GetChild(i);
                if (ch == scroll.transform) continue;
                UnityEngine.Object.Destroy(ch.gameObject);
            }
            return crt;
        }

        // ── body ─────────────────────────────────────────────────────────────────────
        private static void Rebuild()
        {
            if (_rowParent == null || _fp == null) return;
            ClearRows();

            bool center = _marker == MarkerHit.Center;
            if (_titleT != null) _titleT.text = center ? "Dungeon Section" : "Section Edge";
            if (_subT != null) _subT.text = center ? "- room center -" : "- " + FaceWord(_marker).ToLowerInvariant() + " face -";

            if (center) BuildCenter(); else BuildEdge();

            ForceLayout();
        }

        private static void BuildEdge()
        {
            if (_promptT != null) _promptT.text = "how should this wall build?";
            if (_footerT != null) _footerT.text = "tap a choice · a star marks the dungeon's main entrance";
            EdgeTag cur = _fp.EdgeTagOf(_marker);
            foreach (var tag in EdgeChoices)
            {
                var t = tag; bool active = cur == t;
                AddRow(EdgeLabel(t), EdgeBlurb(t), active, true, () => { _fp.SetEdgeTag(_marker, t); Rebuild(); });
            }

            AddDivider("· dungeon entry ·");
            bool isEntrance = _fp.IsEntranceFace(_marker);
            AddRow("Main entrance", isEntrance ? "the dungeon starts at THIS wall" : "make this the dungeon's first door",
                   isEntrance, true, () => { _fp.SetEntranceFace(_marker); Rebuild(); });
        }

        private static void BuildCenter()
        {
            int cx = _fp.FootprintCellsX, cz = _fp.FootprintCellsZ;
            bool multi = cx * cz > 1;
            if (_promptT != null) _promptT.text = "what is this room?";
            if (_footerT != null)
                _footerT.text = multi
                    ? $"saving dices this box into {cx * cz} rooms that place together · tag only the OUTER walls"
                    : "role sets how the generator uses this room · save writes the .blueprint";

            string curRole = _fp.RoleDisplayName();
            foreach (SectionRole role in Enum.GetValues(typeof(SectionRole)))
            {
                var r = role; bool active = string.Equals(curRole, role.ToString(), StringComparison.Ordinal);
                AddRow(role.ToString(), RoleBlurb(role), active, true, () => { _fp.SetRole(r); Rebuild(); });
            }

            AddDivider($"· box size — {cx} x {cz} cells ({cx * 8} x {cz * 8} m) ·");
            AddRow("Wider", "+1 cell east-west", false, false, () => { _fp.AdjustFootprintCells(+1, 0); Rebuild(); });
            AddRow("Narrower", "-1 cell east-west", false, false, () => { _fp.AdjustFootprintCells(-1, 0); Rebuild(); });
            AddRow("Deeper", "+1 cell north-south", false, false, () => { _fp.AdjustFootprintCells(0, +1); Rebuild(); });
            AddRow("Shallower", "-1 cell north-south", false, false, () => { _fp.AdjustFootprintCells(0, -1); Rebuild(); });
            AddRow("Taller", _fp.Stories + (_fp.Stories == 1 ? " story  (+1)" : " stories  (+1)"), false, false, () => { _fp.AdjustStories(+1); Rebuild(); });
            AddRow("Shorter", "take one story off  (-1)", false, false, () => { _fp.AdjustStories(-1); Rebuild(); });

            AddDivider("· dungeon settings ·");
            AddRow("Dungeon type", _fp.DungeonTypeDisplayName() + "  (tap to cycle)", false, false, () => { _fp.CycleDungeonType(+1); Rebuild(); });
            AddRow("Name a new type", "opens a scrap to write on", false, false, () => _fp.OpenDungeonTypeInput());
            AddRow(multi ? "Save room set" : "Save blueprint", multi ? $"dices + saves {cx * cz} rooms" : "keep this room in the archive", false, false, () => SaveSectionInput.Open(_fp));
        }

        // Clone RowTemplate: pencil checkbox (filled red = current choice, hidden for action lines) + hand-written
        // option + a lighter note. Wires the whole row as a button.
        private static void AddRow(string label, string note, bool selected, bool showBox, Action onClick)
        {
            if (_rowTemplate == null) return;
            var row = UnityEngine.Object.Instantiate(_rowTemplate, _rowParent);
            row.name = "Row_" + label; row.SetActive(true);

            var box = row.transform.Find("Box");
            if (box != null)
            {
                box.gameObject.SetActive(showBox);
                var bi = box.GetComponent<Image>();
                if (bi != null) bi.color = selected ? NbRed : NbCheckEmpty;
            }
            var lbl = row.transform.Find("Label")?.GetComponent<TMP_Text>();
            if (lbl != null) { lbl.text = label; lbl.color = selected ? NbInk : NbPencil; lbl.fontStyle = FontStyles.Bold; }
            var nt = row.transform.Find("Note")?.GetComponent<TMP_Text>();
            if (nt != null) nt.text = note ?? "";

            var btn = row.GetComponent<Button>() ?? row.AddComponent<Button>();
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() => { Sfx(); onClick?.Invoke(); });
        }

        // A non-interactive hand-written divider between the role checklist and the settings actions.
        private static void AddDivider(string text)
        {
            if (_rowTemplate == null) return;
            var row = UnityEngine.Object.Instantiate(_rowTemplate, _rowParent);
            row.name = "Divider"; row.SetActive(true);
            row.transform.Find("Box")?.gameObject.SetActive(false);
            var lbl = row.transform.Find("Label")?.GetComponent<TMP_Text>();
            if (lbl != null) { lbl.text = text; lbl.color = NbNote; lbl.fontStyle = FontStyles.Italic; }
            row.transform.Find("Note")?.gameObject.SetActive(false);
            var btn = row.GetComponent<Button>(); if (btn != null) btn.enabled = false;
        }

        private static void ClearRows()
        {
            if (_rowParent == null) return;
            for (int i = _rowParent.childCount - 1; i >= 0; i--)
            {
                var ch = _rowParent.GetChild(i).gameObject;
                if (ch == _rowTemplate) continue;
                UnityEngine.Object.Destroy(ch);
            }
        }

        // ── esc / cursor driver (client-only Hud hook) ───────────────────────────────
        [HarmonyPatch(typeof(Hud), "Update")]
        internal static class Hud_Update_Patch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (IsClientOnly() || Player.m_localPlayer == null) return;
                if (Console.IsVisible() || (Chat.instance != null && Chat.instance.HasFocus())) return;
                if (!IsOpen) return;
                if (_fp == null) { Hide(); return; }              // footprint streamed out
                Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
                SetCrosshair(false);                              // keep the reticle hidden each frame while open
                if (Input.GetKeyDown(KeyCode.Escape)) Hide();
            }
        }

        // ── labels ───────────────────────────────────────────────────────────────────
        private static string EdgeLabel(EdgeTag t)
        {
            switch (t)
            {
                case EdgeTag.Wall: return "Wall";
                case EdgeTag.Doorway: return "Doorway";
                case EdgeTag.Open: return "Open";
                case EdgeTag.Window: return "Window";
                case EdgeTag.Back: return "Back wall";
                default: return t.ToString();
            }
        }
        private static string EdgeBlurb(EdgeTag t)
        {
            switch (t)
            {
                case EdgeTag.Wall: return "solid - no gap";
                case EdgeTag.Doorway: return "a door-sized opening";
                case EdgeTag.Open: return "full arch / wide open";
                case EdgeTag.Window: return "raised window slot";
                case EdgeTag.Back: return "dead-end hint";
                default: return "";
            }
        }
        private static string RoleBlurb(SectionRole r)
        {
            switch (r)
            {
                case SectionRole.Center: return "central chamber / spawn";
                case SectionRole.Edge: return "perimeter room";
                case SectionRole.Doorway: return "transition piece";
                case SectionRole.Corridor: return "long connector";
                case SectionRole.Entry: return "where the dungeon starts";
                case SectionRole.Exit: return "boss / treasure end";
                case SectionRole.Junction: return "crossroads";
                case SectionRole.Transition: return "stairs / ramp between floors";
                default: return "";
            }
        }
        private static string FaceWord(MarkerHit m)
        {
            switch (m)
            {
                case MarkerHit.North: return "North";
                case MarkerHit.East: return "East";
                case MarkerHit.South: return "South";
                case MarkerHit.West: return "West";
                default: return m.ToString();
            }
        }

        // ── helpers (mirror BlueprintBenchPanel) ──────────────────────────────────────
        private static void Sfx() { try { PencilSfx.PlayClick(); } catch { } }

        private static void ForceLayout()
        {
            if (_panel == null) return;
            Canvas.ForceUpdateCanvases();
            foreach (var f in _panel.GetComponentsInChildren<ContentSizeFitter>(true)) { f.enabled = false; f.enabled = true; }
            Canvas.ForceUpdateCanvases();
        }

        private static void SortAboveMap()
        {
            try
            {
                var canvas = _canvasGo.GetComponent<Canvas>();
                int mapOrder = 0;
                var mc = Minimap.instance?.m_largeRoot != null ? Minimap.instance.m_largeRoot.GetComponentInParent<Canvas>() : null;
                if (mc != null) mapOrder = Mathf.Max(mc.sortingOrder, mc.rootCanvas != null ? mc.rootCanvas.sortingOrder : 0);
                canvas.sortingOrder = Mathf.Max(6300, mapOrder + 600);
            }
            catch { }
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            if (UnityEngine.Object.FindObjectOfType<EventSystem>() != null) return;
            var es = new GameObject("FDM_EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            UnityEngine.Object.DontDestroyOnLoad(es);
        }

        private static GameObject Find(string name) => FindRec(_root, name);
        private static GameObject FindRec(Transform r, string name)
        {
            if (r == null) return null;
            if (r.name == name) return r.gameObject;
            for (int i = 0; i < r.childCount; i++) { var g = FindRec(r.GetChild(i), name); if (g != null) return g; }
            return null;
        }
        private static TMP_Text Tmp(string name) => Find(name)?.GetComponent<TMP_Text>();
        private static void Click(string name, Action a) { var b = Find(name)?.GetComponent<Button>(); if (b != null) { b.onClick.RemoveAllListeners(); b.onClick.AddListener(() => a()); } }
    }
}
