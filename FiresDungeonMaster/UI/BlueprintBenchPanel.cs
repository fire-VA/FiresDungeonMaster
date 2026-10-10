using System;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Logging;
using FiresDungeonMaster.Sections;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace FiresDungeonMaster.UI
{
    /// <summary>
    /// BlueprintBenchPanel — runtime controller for the BAKED blueprint-bench UI (the professional "drafting sheet"
    /// section archive; prefab 'FiresBlueprintBench' in the firesdungeonmaster bundle, authored by
    /// FiresDungeonMasterBuilder.BuildBlueprintBench per Tools/BakedUIPipeline). Loads the prefab, wraps it in a
    /// runtime overlay canvas (sorted above the map), binds the baked nodes by name, builds the scroll list with a
    /// runtime RectMask2D (ScrollPool pattern — the baked prefab carries no stencil Mask), populates from
    /// SectionLibrary grouped by dungeon-type, draws the real PLAN VIEW schematic per selection (SectionPlanRenderer),
    /// and wires Edit-in-Box / Place / Delete with pencil-scratch clicks. Draggable by the Header, resizable by the
    /// ResizeHandle grip, modal via InputBlock + ModUiRegistry. Client-only.
    /// </summary>
    public static class BlueprintBenchPanel
    {
        private const string PrefabName = "FiresBlueprintBench";

        private static GameObject _canvasGo, _panel;
        private static Transform _root;
        private static bool _initFailed, _blocked;

        private static RectTransform _content;      // runtime scroll content
        private static RectTransform _listArea;      // fresh stretch-filled container (ConfigPanel-style)
        private static GameObject _catTemplate, _entryTemplate;
        private static Image _planImg;
        private static Sprite _planSprite;          // current schematic sprite (destroyed on replace)
        private static TMP_Text _nameT, _roleV, _typeV, _fpV, _storiesV, _objV, _notesT, _sectCount;

        private static SectionBlueprint _current;
        private static List<SectionBlueprint> _currentSet;   // non-null when the selected entry is a diced set
        private static GameObject _lastRow;
        private static bool _showBlueprints;                 // false = DM sections, true = plain PlanBuild blueprints
        private static TMP_Text _modeLabel;
        private static TMP_InputField _searchInput;          // the NameText node repurposed as a search field
        private static string _filter = "";                  // live name filter typed into the search field
        private static List<SectionBlueprint> _loadedSections = new List<SectionBlueprint>();   // cached so typing doesn't re-read disk
        private static readonly Color SelClear = new Color(1f, 1f, 1f, 0f);
        private static readonly Color SelActive = new Color(1f, 0.824f, 0.541f, 0.28f);   // amber selection

        private static bool IsClientOnly() => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
        public static bool IsOpen => _canvasGo != null && _canvasGo.activeSelf;

        // ── lifecycle ────────────────────────────────────────────────────────────────
        public static void Toggle() { if (IsClientOnly()) return; if (IsOpen) Hide(); else Show(); }

        public static void Show()
        {
            if (IsClientOnly()) return;
            if (!EnsureBuilt()) return;
            _canvasGo.SetActive(true);
            _canvasGo.transform.SetAsLastSibling();
            SortAboveMap();
            if (!_blocked) { FiresCore.UI.InputBlock.Block(true); _blocked = true; }
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            Populate();
        }

        public static void Hide()
        {
            if (_canvasGo != null) _canvasGo.SetActive(false);
            if (_blocked) { FiresCore.UI.InputBlock.Block(false); _blocked = false; }
            SetCrosshair(true);
        }

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

                _canvasGo = new GameObject("FDM_BlueprintBenchCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                UnityEngine.Object.DontDestroyOnLoad(_canvasGo);
                var c = _canvasGo.GetComponent<Canvas>(); c.renderMode = RenderMode.ScreenSpaceOverlay; c.sortingOrder = 6200;
                var s = _canvasGo.GetComponent<CanvasScaler>(); s.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; s.referenceResolution = new Vector2(1920f, 1080f); s.matchWidthOrHeight = 0.5f;
                EnsureEventSystem();

                _panel = UnityEngine.Object.Instantiate(prefab, _canvasGo.transform);
                _root = _panel.transform;
                var rt = _panel.GetComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f); rt.anchoredPosition = Vector2.zero;

                Bind();
                _canvasGo.SetActive(false);
                return true;
            }
            catch (Exception ex) { FiresLogger.LogError($"blueprint bench init failed: {ex}"); _initFailed = true; return false; }
        }

        private static void Bind()
        {
            _nameT = Tmp("NameText"); _roleV = Tmp("RoleValue"); _typeV = Tmp("TypeValue");
            _fpV = Tmp("FootprintValue"); _storiesV = Tmp("StoriesValue"); _objV = Tmp("ObjectsValue");
            _notesT = Tmp("NotesText"); _sectCount = Tmp("SectCount");
            _planImg = Find("PlanView")?.GetComponent<Image>();

            Click("CloseButton", () => { Sfx(); Hide(); });
            Click("EditButton", OnEdit);
            Click("PlaceButton", OnPlace);
            Click("DeleteButton", OnDelete);

            // Drag by the header, resize by the corner grip (the pipeline affordances).
            var header = Find("Header");
            if (header != null) { var d = header.GetComponent<DragMove>() ?? header.AddComponent<DragMove>(); d.Target = _panel.GetComponent<RectTransform>(); }
            var grip = Find("ResizeHandle");
            if (grip != null)
            {
                var img = grip.GetComponent<Image>(); if (img != null) img.raycastTarget = true;
                GripFix.Apply(grip, "BlueprintBenchPanel");   // 0.1.128: a real grip, not a plain square
                var r = grip.GetComponent<DragResize>() ?? grip.AddComponent<DragResize>(); r.Target = _panel.GetComponent<RectTransform>(); r.MinSize = new Vector2(760f, 480f);
            }

            _catTemplate = Find("CategoryTemplate");
            _entryTemplate = Find("EntryTemplate");
            _content = BuildPoolScroll();
            BuildModeToggle();
            BuildSearchField();
            FiresLogger.LogInfo($"[BENCH-DBG] bound: catTemplate={( _catTemplate != null)} entryTemplate={(_entryTemplate != null)} content={(_content != null)}");
        }

        // Replace the baked NameText (the big title above the plan view) with a live SEARCH field: it shows the selected
        // blueprint's name, and typing into it filters the SAVED SECTIONS list by name. Built as its OWN GameObject at
        // NameText's rect — an Image cannot be added onto the label itself (two Graphics on one GameObject is illegal,
        // which is what silently broke the first attempt), and TMP_InputField needs its text component on a CHILD
        // viewport, not on the field's root. Standard TMP hierarchy: Field(Image+TMP_InputField) → Text Area(RectMask2D)
        // → Text + Placeholder.
        private static void BuildSearchField()
        {
            if (_nameT == null || _panel == null) return;
            var nameRt = _nameT.rectTransform;

            var field = new GameObject("BenchSearch", typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
            field.transform.SetParent(nameRt.parent, false);
            field.transform.SetSiblingIndex(nameRt.GetSiblingIndex());
            var frt = (RectTransform)field.transform;
            frt.anchorMin = nameRt.anchorMin; frt.anchorMax = nameRt.anchorMax; frt.pivot = nameRt.pivot;
            frt.anchoredPosition = nameRt.anchoredPosition; frt.sizeDelta = nameRt.sizeDelta;
            var bg = field.GetComponent<Image>();
            bg.color = new Color(0.06f, 0.16f, 0.32f, 0.55f);
            var ol = field.AddComponent<Outline>();
            ol.effectColor = new Color(0.44f, 0.62f, 0.82f, 0.6f); ol.effectDistance = new Vector2(1.5f, -1.5f);

            var area = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
            area.transform.SetParent(field.transform, false);
            var art = (RectTransform)area.transform;
            art.anchorMin = Vector2.zero; art.anchorMax = Vector2.one; art.offsetMin = new Vector2(8f, 2f); art.offsetMax = new Vector2(-8f, -2f);

            TextMeshProUGUI MakeText(string name)
            {
                var t = new GameObject(name, typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
                t.transform.SetParent(area.transform, false);
                var trt = t.rectTransform; trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = trt.offsetMax = Vector2.zero;
                if (_nameT.font != null) t.font = _nameT.font;
                t.fontSize = _nameT.fontSize; t.alignment = TextAlignmentOptions.MidlineLeft;
                t.enableWordWrapping = false; t.overflowMode = TextOverflowModes.Ellipsis; t.raycastTarget = false;
                return t;
            }

            var text = MakeText("Text");
            text.color = _nameT.color;
            var ph = MakeText("Placeholder");
            ph.fontStyle = FontStyles.Italic; ph.color = new Color(0.56f, 0.69f, 0.90f, 0.55f); ph.text = "Search blueprints…";

            var input = field.GetComponent<TMP_InputField>();
            input.textViewport = art;
            input.textComponent = text;
            input.placeholder = ph;
            input.targetGraphic = bg;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.customCaretColor = true; input.caretColor = new Color(0.92f, 0.95f, 1f, 1f); input.caretWidth = 2;
            input.selectionColor = new Color(0.40f, 0.62f, 0.82f, 0.5f);
            input.onValueChanged.AddListener(OnSearchChanged);
            _searchInput = input;

            _nameT.gameObject.SetActive(false);   // the field now owns the name display
        }

        // Typing in the search field filters the cached list (no disk re-read) without disturbing the current selection.
        private static void OnSearchChanged(string value)
        {
            _filter = value ?? "";
            BuildList();
        }

        // A header toggle that flips the left list between DM SECTIONS (#fdm_ blueprints) and plain PlanBuild
        // BLUEPRINTS. In blueprint mode, Edit drops the blueprint into a bounds-fitted box and Place hands it to
        // Infinity Hammer's hammer_blueprint command.
        private static void BuildModeToggle()
        {
            if (_panel == null) return;
            var go = new GameObject("ModeToggle", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(_panel.transform, false);
            var rt = (RectTransform)go.transform;
            // Sit as a tab on top of the DRAWN BY title block (bottom-left, anchored 0.035..0.45 x, top edge y=0.17),
            // spanning its width — instead of floating over the SAVED SECTIONS header.
            rt.anchorMin = new Vector2(0.035f, 0.17f); rt.anchorMax = new Vector2(0.45f, 0.17f); rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, 3f); rt.sizeDelta = new Vector2(0f, 26f);
            go.GetComponent<Image>().color = new Color(0.10f, 0.26f, 0.48f, 0.92f);
            var ol = go.AddComponent<Outline>(); ol.effectColor = new Color(0.44f, 0.62f, 0.82f, 0.7f); ol.effectDistance = new Vector2(1.5f, -1.5f);

            var lblGo = new GameObject("Label", typeof(RectTransform));
            lblGo.transform.SetParent(go.transform, false);
            var lrt = (RectTransform)lblGo.transform; lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = new Vector2(8, 0); lrt.offsetMax = new Vector2(-8, 0);
            var lt = lblGo.AddComponent<TextMeshProUGUI>(); lt.fontSize = 14f; lt.alignment = TextAlignmentOptions.Center; lt.color = new Color(0.918f, 0.953f, 1f, 1f); lt.raycastTarget = false;
            if (_sectCount != null && _sectCount.font != null) lt.font = _sectCount.font;
            _modeLabel = lt;

            go.GetComponent<Button>().onClick.AddListener(() =>
            {
                Sfx();
                _showBlueprints = !_showBlueprints;
                _current = null; _currentSet = null;
                UpdateModeLabel();
                Populate();
            });
            UpdateModeLabel();
        }

        private static void UpdateModeLabel()
        {
            if (_modeLabel != null) _modeLabel.text = _showBlueprints ? "◄ PlanBuild Blueprints ►" : "◄ DM Sections ►";
        }

        // ── scroll (baked SectionPool → runtime RectMask2D) ──────────────────────────
        private static RectTransform BuildPoolScroll()
        {
            var pool = Find("SectionPool")?.transform;
            if (pool == null) { FiresLogger.LogError("[BENCH-DBG] SectionPool NOT FOUND on baked prefab — list cannot build."); return null; }
            var list = pool.parent as RectTransform;
            if (list == null) { FiresLogger.LogError("[BENCH-DBG] SectionPool has no List parent."); return null; }

            // The baked SectionPool's runtime height keeps collapsing to 0 no matter how it's re-anchored (viewport=Wx0).
            // So IGNORE it: hide the baked pool and build a FRESH area that stretch-fills the List below the ~34px
            // header, then the EXACT ConfigPanel BuildScroll structure inside it (that F8 panel scrolls correctly).
            pool.gameObject.SetActive(false);

            var area = new GameObject("BenchListArea", typeof(RectTransform));
            area.transform.SetParent(list, false);
            _listArea = (RectTransform)area.transform;
            // Fill the List column below its ~34px header, but STOP ~48px above its bottom edge: the TitleBlock footer
            // (DRAWN BY / SCALE / SECTIONS) sits at panel-y 0.045–0.17 and its top pokes above the List's 0.135 bottom in
            // the SAME x-range, so without this margin the last rows (and the RectMask2D) bleed over the footer labels.
            _listArea.anchorMin = Vector2.zero; _listArea.anchorMax = Vector2.one; _listArea.offsetMin = new Vector2(0f, 48f); _listArea.offsetMax = new Vector2(0f, -34f);

            var scrollGo = new GameObject("Scroll", typeof(RectTransform)); scrollGo.transform.SetParent(area.transform, false);
            var srt = (RectTransform)scrollGo.transform; srt.anchorMin = Vector2.zero; srt.anchorMax = Vector2.one; srt.offsetMin = srt.offsetMax = Vector2.zero;
            var vp = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D)); vp.transform.SetParent(scrollGo.transform, false);
            var vprt = (RectTransform)vp.transform; vprt.anchorMin = Vector2.zero; vprt.anchorMax = Vector2.one; vprt.offsetMin = Vector2.zero; vprt.offsetMax = new Vector2(-14f, 0f);
            vp.GetComponent<Image>().color = Color.clear;
            var content = new GameObject("Content", typeof(RectTransform)); content.transform.SetParent(vp.transform, false);
            var crt = (RectTransform)content.transform;
            crt.anchorMin = new Vector2(0, 1); crt.anchorMax = new Vector2(1, 1); crt.pivot = new Vector2(0, 1); crt.sizeDelta = Vector2.zero;   // ConfigPanel-exact
            var vlg = content.AddComponent<VerticalLayoutGroup>(); vlg.childControlWidth = vlg.childForceExpandWidth = true; vlg.childControlHeight = true; vlg.childForceExpandHeight = false; vlg.spacing = 8f;
            content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var sr = scrollGo.AddComponent<ScrollRect>(); sr.horizontal = false; sr.vertical = true; sr.viewport = vprt; sr.content = crt; sr.movementType = ScrollRect.MovementType.Clamped; sr.scrollSensitivity = 600f;   // ~20x the default so the long blueprint list scrolls fast
            BuildScrollbar(scrollGo.transform, sr, new Color(0.06f, 0.16f, 0.32f, 0.6f), new Color(0.44f, 0.62f, 0.82f, 0.9f));

            if (_catTemplate != null) { _catTemplate.transform.SetParent(crt, false); _catTemplate.SetActive(false); }
            if (_entryTemplate != null) { _entryTemplate.transform.SetParent(crt, false); _entryTemplate.SetActive(false); }
            return crt;
        }

        // A vertical scrollbar pinned to the right edge of the scroll, wired to sr.verticalScrollbar.
        internal static void BuildScrollbar(Transform scroll, ScrollRect sr, Color track, Color handle)
        {
            var sb = new GameObject("Scrollbar", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
            sb.transform.SetParent(scroll, false);
            var sbrt = (RectTransform)sb.transform; sbrt.anchorMin = new Vector2(1, 0); sbrt.anchorMax = new Vector2(1, 1); sbrt.pivot = new Vector2(1, 1); sbrt.sizeDelta = new Vector2(11, 0); sbrt.anchoredPosition = Vector2.zero;
            sb.GetComponent<Image>().color = track;
            var area = new GameObject("SlidingArea", typeof(RectTransform));
            area.transform.SetParent(sb.transform, false);
            var art = (RectTransform)area.transform; art.anchorMin = Vector2.zero; art.anchorMax = Vector2.one; art.offsetMin = art.offsetMax = Vector2.zero;
            var h = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            h.transform.SetParent(area.transform, false);
            var hrt = (RectTransform)h.transform; hrt.anchorMin = Vector2.zero; hrt.anchorMax = Vector2.one; hrt.offsetMin = hrt.offsetMax = Vector2.zero;
            h.GetComponent<Image>().color = handle;
            var scr = sb.GetComponent<Scrollbar>(); scr.handleRect = hrt; scr.direction = Scrollbar.Direction.BottomToTop; scr.targetGraphic = h.GetComponent<Image>();
            sr.verticalScrollbar = scr; sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        }

        // ── populate (SectionLibrary → grouped list) ─────────────────────────────────
        public static void Populate()
        {
            if (_content == null) return;
            ResetDetail();     // clears the search field + detail panel
            _filter = "";

            // PlanBuild mode reads the .blueprint folders on disk; DM mode reads the mod's own section library. Cache the
            // result so typing in the search field re-filters without re-reading disk each keystroke.
            if (_showBlueprints)
            {
                _loadedSections = LoadPlanBuildBlueprints();
            }
            else
            {
                try { _loadedSections = FdmPaths.OpenLibrary().ListAllSections().Where(s => s.IsDungeonSection).ToList(); }
                catch (Exception ex) { FiresLogger.LogError($"bench library load failed: {ex.Message}"); _loadedSections = new List<SectionBlueprint>(); }
            }
            BuildList();
        }

        // Render the cached sections into the grouped list, honouring the live search filter. Called by Populate (full
        // reload) and by OnSearchChanged (filter only — no disk re-read, no detail reset).
        private static void BuildList()
        {
            if (_content == null || _catTemplate == null || _entryTemplate == null) return;
            ClearList();

            var sections = _loadedSections ?? new List<SectionBlueprint>();

            // Collapse a diced set's N cells into ONE display unit (grouped by SetId); standalone sections stay as-is.
            var setGroups = sections.Where(s => s.IsSetMember)
                .GroupBy(s => s.SetId)
                .Select(g => g.OrderBy(s => s.SetPos.y).ThenBy(s => s.SetPos.x).ToList())
                .ToList();
            var standalone = sections.Where(s => !s.IsSetMember).ToList();

            var units = new List<(string type, SectionBlueprint rep, List<SectionBlueprint> set)>();
            foreach (var s in standalone) units.Add((TypeKey(s.DungeonType), s, null));
            foreach (var set in setGroups) units.Add((TypeKey(set[0].DungeonType), set[0], set));

            int total = units.Count;
            string f = _filter != null ? _filter.Trim() : "";
            if (f.Length > 0)
                units = units.Where(u => UnitLabel(u.rep, u.set).IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            if (_sectCount != null) _sectCount.text = total + " ON FILE" + (f.Length > 0 ? $"  ·  {units.Count} SHOWN" : "");

            var groups = units
                .GroupBy(u => u.type, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                Transform parent = AddCategory(g.Key.ToUpperInvariant());
                foreach (var u in g.OrderBy(u => UnitLabel(u.rep, u.set), StringComparer.OrdinalIgnoreCase))
                    AddEntry(parent, u.rep, u.set);
            }
            ForceLayout();
        }

        // ── PlanBuild folder reader (mirrors FAP's VABlueprintLoader roots) ──────────────
        // Parse every .blueprint/.vbuild found in the PlanBuild / Infinity-Hammer / FAP drop folders into a
        // SectionBlueprint (same IH format the schema already reads). De-duped by full path.
        private static List<SectionBlueprint> LoadPlanBuildBlueprints()
        {
            var result = new List<SectionBlueprint>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in EnumeratePlanBuildFiles())
            {
                string full;
                try { full = System.IO.Path.GetFullPath(path); } catch { continue; }
                if (!seen.Add(full)) continue;
                try
                {
                    var bp = SectionSchema.ParseFile(path);
                    if (bp == null || bp.Objects == null || bp.Objects.Count == 0) continue;
                    if (bp.IsDungeonSection) continue;   // DM sections show under the DM Sections tab, not PlanBuild Blueprints
                    // FAP-style: the FILE name IS the saved blueprint name — the in-file #name is PlanBuild's auto
                    // "Multiple (<count>)", which is what was showing wrong.
                    bp.Name = System.IO.Path.GetFileNameWithoutExtension(path);
                    bp.SectionId = bp.Name;
                    bp.SourcePath = full;   // kept so Place can hand the real file to Infinity Hammer's own command
                    result.Add(bp);
                }
                catch (Exception ex) { FiresLogger.LogWarning($"planbuild parse '{System.IO.Path.GetFileName(path)}': {ex.Message}"); }
            }
            FiresLogger.LogInfo($"PlanBuild reader: {result.Count} blueprint(s) found.");
            return result;
        }

        private static IEnumerable<string> EnumeratePlanBuildFiles()
        {
            string cfg = BepInEx.Paths.ConfigPath;
            var roots = new List<string>
            {
                System.IO.Path.Combine(cfg, "PlanBuild", "blueprints"),
                System.IO.Path.Combine(cfg, "PlanBuild"),
                System.IO.Path.Combine(cfg, "FiresAdminPrefabs", "HammerTabs", "Blueprints"),
                System.IO.Path.Combine(cfg, "InfinityHammer"),
            };
            string steam = GuessSteamCommon();
            if (!string.IsNullOrEmpty(steam))
            {
                roots.Add(System.IO.Path.Combine(steam, "Valheim", "BepInEx", "config", "PlanBuild", "blueprints"));
                roots.Add(System.IO.Path.Combine(steam, "Valheim", "BepInEx", "config", "PlanBuild"));
                roots.Add(System.IO.Path.Combine(steam, "Valheim dedicated server", "BepInEx", "config", "PlanBuild", "blueprints"));
            }
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root)) continue;
                string[] files;
                try { files = System.IO.Directory.GetFiles(root, "*.*", System.IO.SearchOption.TopDirectoryOnly); }
                catch { continue; }
                foreach (var f in files)
                {
                    var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".blueprint" || ext == ".vbuild") yield return f;
                }
            }
        }

        private static string GuessSteamCommon()
        {
            try
            {
                string here = AppDomain.CurrentDomain.BaseDirectory;
                int idx = here.IndexOf(System.IO.Path.DirectorySeparatorChar + "common" + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                if (idx > 0) return here.Substring(0, idx + ("\\common\\").Length);
            }
            catch { }
            return @"C:\Program Files (x86)\Steam\steamapps\common\";
        }

        // FLAT list: the category header and its entries are all direct children of the scroll content (one VLG),
        // rather than nesting entries under a category that carries its own VLG + ContentSizeFitter. A ContentSizeFitter
        // inside a childControlHeight parent layout collapses to 0 height unless it resolves in a specific order — the
        // exact fragility that left the runtime list blank. So we strip the clone's fitter and pin an explicit height.
        private static Transform AddCategory(string label)
        {
            var cat = UnityEngine.Object.Instantiate(_catTemplate, _content);
            cat.name = "Cat_" + label; cat.SetActive(true);
            foreach (var f in cat.GetComponents<ContentSizeFitter>()) UnityEngine.Object.Destroy(f);
            var le = cat.GetComponent<LayoutElement>() ?? cat.AddComponent<LayoutElement>(); le.minHeight = 34f; le.preferredHeight = 34f;
            var nm = cat.transform.Find("Button/Name")?.GetComponent<TMP_Text>() ?? cat.GetComponentInChildren<TMP_Text>(true);
            if (nm != null) nm.text = label;
            return _content;   // entries become siblings of the header, flat in the content VLG
        }

        private static void AddEntry(Transform categoryParent, SectionBlueprint rep, List<SectionBlueprint> setCells)
        {
            bool isSet = setCells != null;
            var e = UnityEngine.Object.Instantiate(_entryTemplate, categoryParent);
            e.SetActive(true);
            var nm = e.transform.Find("Name")?.GetComponent<TMP_Text>();
            if (nm != null) nm.text = UnitLabel(rep, setCells);
            var meta = e.transform.Find("Meta")?.GetComponent<TMP_Text>();
            if (meta != null) meta.text = isSet ? $"{rep.SetSize.x}x{rep.SetSize.y} set" : $"{rep.Role} {rep.Footprint.x}x{rep.Footprint.y}";
            var sel = e.transform.Find("Selection")?.GetComponent<Image>();
            if (sel != null) sel.color = SelClear;
            var btn = e.GetComponent<Button>() ?? e.AddComponent<Button>();
            btn.onClick.RemoveAllListeners();
            var capRep = rep; var capSet = setCells; var capGo = e;
            btn.onClick.AddListener(() => { Sfx(); ShowUnit(capGo, capRep, capSet); });
        }

        // ── selection / detail ───────────────────────────────────────────────────────
        private static void ShowUnit(GameObject row, SectionBlueprint rep, List<SectionBlueprint> setCells)
        {
            bool isSet = setCells != null;
            _current = rep; _currentSet = setCells; _lastRow = row;
            ClearSelections();
            var sel = row.transform.Find("Selection")?.GetComponent<Image>(); if (sel != null) sel.color = SelActive;

            string unitLabel = UnitLabel(rep, setCells);
            if (_searchInput != null) _searchInput.SetTextWithoutNotify(unitLabel);   // show the selected name in the search field
            else if (_nameT != null) _nameT.text = unitLabel;
            if (_roleV != null) _roleV.text = isSet ? "room set" : rep.Role.ToString();
            if (_typeV != null) _typeV.text = string.IsNullOrEmpty(rep.DungeonType) ? "(none)" : rep.DungeonType;
            if (_fpV != null) _fpV.text = isSet ? $"{rep.SetSize.x} x {rep.SetSize.y} cells" : $"{rep.Footprint.x} x {rep.Footprint.y}";
            if (_storiesV != null) _storiesV.text = rep.Stories.ToString();
            int objs = isSet ? setCells.Sum(RealCount) : RealCount(rep);
            if (_objV != null) _objV.text = objs.ToString();
            if (_notesT != null) _notesT.text = isSet
                ? $"{setCells.Count}-room set — places together as one {rep.SetSize.x}x{rep.SetSize.y} block."
                : (string.IsNullOrWhiteSpace(rep.Description) ? "" : rep.Description);
            // Show a real rendered 3D thumbnail of the blueprint (like KG's preview); fall back to the schematic plan
            // if the render can't run (headless, empty, or all pieces unresolved).
            var previewBp = isSet ? CombineSetForPreview(setCells) : rep;
            var thumb = BlueprintPreviewRenderer.RenderSprite(previewBp);
            if (thumb != null && _planImg != null)
            {
                _planImg.sprite = thumb;
                _planImg.type = Image.Type.Simple;
                _planImg.preserveAspect = true;
                _planImg.color = Color.white;
                _planImg.enabled = true;
            }
            else
            {
                DrawPlan(previewBp);
            }
        }

        private static int RealCount(SectionBlueprint bp) => bp.Objects.Count(p => !SectionCenterpoint.Is(p));

        // Display label for a unit: a standalone section's name, or a set's base name + "(NxM set)".
        private static string UnitLabel(SectionBlueprint rep, List<SectionBlueprint> setCells)
        {
            if (setCells == null) return string.IsNullOrEmpty(rep.Name) ? rep.SectionId : rep.Name;
            string n = string.IsNullOrEmpty(rep.Name) ? rep.SectionId : rep.Name;
            int idx = n.LastIndexOf("_r", StringComparison.Ordinal);   // strip the per-cell "_r{j}c{i}" suffix
            if (idx > 0) n = n.Substring(0, idx);
            return $"{n}  ({rep.SetSize.x}x{rep.SetSize.y} set)";
        }

        private static string TypeKey(string t) => string.IsNullOrEmpty(t) ? "(no type)" : t;

        // Synthesize a single-frame preview blueprint from a set's cells: perimeter edges + every cell's objects
        // offset by its grid position, so the PLAN VIEW shows the whole assembled set. Not saved — display only.
        private static SectionBlueprint CombineSetForPreview(List<SectionBlueprint> cells)
        {
            var rep = cells[0];
            int X = Mathf.Max(1, rep.SetSize.x), Z = Mathf.Max(1, rep.SetSize.y);
            var c = new SectionBlueprint
            {
                Footprint = new Vector2Int(X, Z),
                Stories = rep.Stories,
                Role = rep.Role,
                DungeonType = rep.DungeonType,
                EdgeTags = new Dictionary<string, List<string>>
                {
                    { "north", EdgeOf(cells, x => x.SetPos.y == Z - 1, "north") },
                    { "south", EdgeOf(cells, x => x.SetPos.y == 0, "south") },
                    { "east",  EdgeOf(cells, x => x.SetPos.x == X - 1, "east") },
                    { "west",  EdgeOf(cells, x => x.SetPos.x == 0, "west") },
                },
            };
            foreach (var cell in cells)
            {
                Vector3 off = SectionPlacer.SetCellOffset(cell.SetPos.x, cell.SetPos.y, X, Z);
                foreach (var o in cell.Objects)
                {
                    if (o == null || SectionCenterpoint.Is(o)) continue;
                    c.Objects.Add(new FiresDungeonMaster.Sections.Piece { Prefab = o.Prefab, Pos = off + o.Pos, Rot = o.Rot, Scale = o.Scale, Chance = 1f });
                }
            }
            return c;
        }

        private static List<string> EdgeOf(List<SectionBlueprint> cells, Func<SectionBlueprint, bool> pick, string face)
        {
            foreach (var cell in cells)
                if (pick(cell) && cell.EdgeTags != null && cell.EdgeTags.TryGetValue(face, out var l) && l != null)
                    return new List<string>(l);
            return new List<string>();
        }

        // Draw the REAL schematic from section data into the PlanView (replaces the baked sample sprite).
        private static void DrawPlan(SectionBlueprint section)
        {
            if (_planImg == null) return;
            try
            {
                var tex = SectionPlanRenderer.Render(section, 320);
                var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                if (_planSprite != null) { var old = _planSprite.texture; UnityEngine.Object.Destroy(_planSprite); if (old != null) UnityEngine.Object.Destroy(old); }
                _planSprite = sp;
                _planImg.sprite = sp; _planImg.preserveAspect = true; _planImg.color = Color.white;
            }
            catch (Exception ex) { FiresLogger.LogWarning($"plan draw failed: {ex.Message}"); }
        }

        // ── button handlers ──────────────────────────────────────────────────────────
        private static void OnEdit()
        {
            Sfx();
            // Blueprint mode: drop the raw blueprint into a bounds-fitted box on the hammer.
            if (_showBlueprints)
            {
                if (_current == null) return;
                var bp = _current; Hide();
                Message(SectionEditSpawner.ArmBlueprintForPlacement(bp));
                return;
            }
            // DM section mode: arm the edit onto the hammer, close the bench, and let the player place the box where
            // they want — the next footprint they hammer-place becomes this section/set (configured + filled).
            if (_currentSet != null) { var cells = _currentSet; Hide(); Message(SectionEditSpawner.ArmSetForPlacement(cells)); return; }
            if (_current == null) return;
            string id = _current.SectionId; Hide();
            Message(SectionEditSpawner.ArmForPlacementById(id));
        }

        // Blueprint mode Place: hand the blueprint to Infinity Hammer's OWN hammer_blueprint command so you get the
        // real IH ghost — name announced, full rotation/nudge/scale/snap placement. IH only reads its own blueprint
        // folders, so InfinityHammerPlacement copies the file there first (that's why the bare command "did nothing"
        // before). If IH isn't loaded, fall back to our translucent grab (aim + left-click drops it at the crosshair).
        private static void PlaceBlueprintOntoHammer()
        {
            if (_current == null) return;
            var bp = _current; Hide();
            if (FiresDungeonMaster.Editor.InfinityHammerPlacement.TryPlace(bp, out string ihMsg)) { Message(ihMsg); return; }
            Message(SectionEditSpawner.ArmForRawPlace(bp));
        }

        private static void OnPlace()
        {
            Sfx();
            if (_showBlueprints) { PlaceBlueprintOntoHammer(); return; }
            if (_current == null && _currentSet == null) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) { Message("place must run on the host/server."); return; }
            var p = Player.m_localPlayer; if (p == null) return;
            Vector3 dir = p.transform.forward; dir.y = 0f; dir = dir.sqrMagnitude < 0.01f ? Vector3.forward : dir.normalized;
            Vector3 pos = p.transform.position + dir * 6f; var rot = Quaternion.LookRotation(dir, Vector3.up);
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(pos, out float gh)) pos.y = gh;
            Message(_currentSet != null
                ? SectionPlacer.PlaceSet(_currentSet, pos, rot).Message
                : SectionPlacer.PlaceById(_current.SectionId, pos, rot).Message);
        }

        private static void OnDelete()
        {
            Sfx();
            try
            {
                var lib = FdmPaths.OpenLibrary();
                if (_currentSet != null)
                {
                    int n = 0; foreach (var c in _currentSet) if (lib.DeleteSectionById(c.SectionId)) n++;
                    Message($"deleted {n}-room set.");
                    _current = null; _currentSet = null; Populate();
                    return;
                }
                if (_current == null) return;
                bool ok = lib.DeleteSectionById(_current.SectionId);
                Message(ok ? $"deleted '{_current.SectionId}'." : "delete failed.");
                _current = null; Populate();
            }
            catch (Exception ex) { Message("delete failed: " + ex.Message); }
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
                Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
                SetCrosshair(false);
                if (Input.GetKeyDown(KeyCode.Escape)) Hide();
            }
        }

        // ── helpers ──────────────────────────────────────────────────────────────────
        private static void Sfx() { try { FiresCore.UI.PencilSfx.PlayClick(); } catch { } }
        private static void Message(string m) { FiresLogger.LogInfo(m); Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "[FiresDungeonMaster] " + m); }

        private static void ClearList()
        {
            if (_content == null) return;
            for (int i = _content.childCount - 1; i >= 0; i--)
            {
                var ch = _content.GetChild(i).gameObject;
                if (ch == _catTemplate || ch == _entryTemplate) continue;
                UnityEngine.Object.Destroy(ch);
            }
        }

        private static void ResetDetail()
        {
            _current = null; _currentSet = null; _lastRow = null;
            if (_searchInput != null) _searchInput.SetTextWithoutNotify("");
            else if (_nameT != null) _nameT.text = "";
            foreach (var t in new[] { _roleV, _typeV, _fpV, _storiesV, _objV }) if (t != null) t.text = "";
            if (_notesT != null) _notesT.text = "Select a section.";
            if (_planImg != null) { _planImg.sprite = null; _planImg.color = new Color(0.06f, 0.19f, 0.38f, 0.3f); }
        }

        private static void ClearSelections()
        {
            if (_content == null) return;
            foreach (var img in _content.GetComponentsInChildren<Image>(true))
                if (img.gameObject.name == "Selection") img.color = SelClear;
        }

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
                canvas.sortingOrder = Mathf.Max(6200, mapOrder + 500);
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
