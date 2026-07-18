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
    /// SectionBrowserUI — the SAVED-SECTION BROWSER. A clean, single-purpose repurpose of KG Blueprint's imported
    /// blueprint browser prefab (cloned as 'FiresSectionBrowserUI' into the 'firesdungeonmaster' bundle, KG scripts
    /// stripped — only the Canvas/UI hierarchy is reused). It lists every saved section grouped by DungeonType
    /// (one collapsible CategoryEntry per type, one Entry per section), shows the selected section's name /
    /// description / role+footprint in the Main detail panel, and the Select button PLACES it through the same
    /// SectionPlacer the firesdungeon_place console command drives.
    ///
    /// Mirrors kg_Blueprint.BlueprintUI.Init wiring (Content / Entry / CategoryEntry / Main / Select by transform
    /// path) but is driven entirely from OUR SectionLibrary data — no KG BlueprintRoot, no foreign sources, no
    /// clipboard / convert / model-view (those KG features are intentionally OUT of scope; see the class header
    /// "deferred" list).
    ///
    /// CLIENT-ONLY: never instantiated on a dedicated server (IsClientOnly graphics guard on every entry point), so
    /// the Hud/Canvas/TMP refs never load headless. Placement remains server-authoritative — Select refuses on a
    /// non-host client with a guidance message, exactly like firesdungeon_place.
    /// </summary>
    public static class SectionBrowserUI
    {
        internal const string UiPrefabName = "FiresSectionBrowserUI";

        // The cloned KG canvas is drawn ABOVE the Hud: KG ships its UI on a sorting layer that collides with the
        // game's HUD, so a re-baked clone routinely renders behind it (or off-mode). We force the baked canvas to a
        // known-good ScreenSpaceOverlay at this order, matching DungeonTypeInput's standalone canvas (sortingOrder 5000).
        private const int OverlaySortingOrder = 5000;

        private static GameObject _ui;
        private static Canvas _canvas;
        private static Transform _main;
        private static Transform _content;
        private static GameObject _entryTemplate;
        private static GameObject _categoryTemplate;
        private static Button _selectButton;
        private static TMP_Text _selectButtonText;
        private static TMP_Text _nameText;
        private static TMP_Text _descriptionText;
        private static TMP_Text _authorText;
        private static bool _initFailed;

        // The section the Select button will place. Null = nothing selected (Main panel hidden).
        private static SectionBlueprint _current;
        private static GameObject _lastPressedEntry;
        private static bool _blocked;   // we hold the InputBlock modal gate while the browser is open

        private static readonly Color SelectionClear = new Color(1f, 1f, 1f, 0f);
        private static readonly Color SelectionActive = Color.green;

        private static bool IsClientOnly()
            => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

        private static bool IsVisible => _ui != null && _ui.activeSelf;

        // ── lifecycle ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Load the cloned UI prefab from the 'firesdungeonmaster' bundle, DontDestroyOnLoad it hidden, and bind the
        /// Content / Entry / CategoryEntry / Main / Select transforms (mirroring KG's Init paths). Idempotent — a
        /// second call no-ops once the UI exists. No-op headless. Returns true when the UI is ready.
        /// </summary>
        public static bool Init()
        {
            if (IsClientOnly()) return false;
            if (_ui != null) return true;
            if (_initFailed) return false;

            try
            {
                GameObject prefab = FootprintRegistration.LoadUiPrefab(UiPrefabName);
                if (prefab == null)
                {
                    FiresLogger.LogWarning($"section browser prefab '{UiPrefabName}' not in bundle — rebake the 'firesdungeonmaster' bundle.");
                    _initFailed = true;
                    return false;
                }

                FiresLogger.LogInfo($"[browser-diag] prefab '{UiPrefabName}' loaded from bundle = {(prefab != null ? prefab.name : "NULL")}");

                _ui = UnityEngine.Object.Instantiate(prefab);
                UnityEngine.Object.DontDestroyOnLoad(_ui);
                _ui.SetActive(false);
                FiresLogger.LogInfo($"[browser-diag] instance created = {(_ui != null)} (activeSelf={_ui.activeSelf})");

                if (!BindHierarchy())
                {
                    FiresLogger.LogError("section browser: required transforms missing — KG prefab paths changed.");
                    UnityEngine.Object.Destroy(_ui);
                    _ui = null;
                    _initFailed = true;
                    return false;
                }

                // ROOT-CAUSE FIX for the "command logs toggled but nothing renders" bug: the baked KG canvas does NOT
                // render as a top-level overlay on its own (cloned canvases come through with a render mode / sorting
                // order that lands them behind the Hud, and with no GraphicRaycaster/EventSystem in a fresh scene they
                // are also non-interactive). NormalizeCanvas forces it to ScreenSpaceOverlay at a high sortingOrder and
                // guarantees a raycaster + EventSystem, matching the proven DungeonTypeInput standalone-canvas path.
                NormalizeCanvas();

                WireStaticButtons();
                // Register with the shared modal registry so the game frees the OS cursor + treats us as an open
                // panel while visible (same path the leaderboard / ConfigPanel use).
                FiresCore.Bridge.ModUiRegistry.Register("FDM_SectionBrowser", () => IsVisible, Hide, () => _ui);
                FiresLogger.LogInfo("section browser UI initialized.");
                return true;
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"section browser Init failed: {ex}");
                _initFailed = true;
                return false;
            }
        }

        // Mirror BlueprintUI.Init transform paths. The cloned prefab keeps the exact Canvas/UI subtree, so these
        // Finds resolve against the same hierarchy KG shipped. Any null here means the clone diverged.
        private static bool BindHierarchy()
        {
            Transform root = _ui.transform;

            // The cloned KG subtree is <root>/Canvas/UI/... — "Canvas" is the GameObject carrying the Canvas component.
            // Fall back to any Canvas in the clone so a renamed root can't silently strand us with no overlay.
            Transform canvasTransform = root.Find("Canvas");
            _canvas = canvasTransform != null
                ? canvasTransform.GetComponent<Canvas>()
                : _ui.GetComponentInChildren<Canvas>(true);

            _main = root.Find("Canvas/UI/Main");
            _entryTemplate = root.Find("Canvas/UI/List/Scroll View/Viewport/Content/Entry")?.gameObject;
            _categoryTemplate = root.Find("Canvas/UI/List/Scroll View/Viewport/Content/CategoryEntry")?.gameObject;

            if (_main == null || _entryTemplate == null || _categoryTemplate == null)
                return false;

            _content = _entryTemplate.transform.parent;
            _entryTemplate.SetActive(false);
            _categoryTemplate.SetActive(false);

            _selectButton = _main.Find("Select")?.GetComponent<Button>();
            _selectButtonText = _main.Find("Select/text")?.GetComponent<TMP_Text>();
            _nameText = _main.Find("Name")?.GetComponent<TMP_Text>();
            _descriptionText = _main.Find("Description")?.GetComponent<TMP_Text>();
            _authorText = _main.Find("Author")?.GetComponent<TMP_Text>();

            // Select is the one button we hard-require; the TMP labels are best-effort (a missing one is skipped).
            return _selectButton != null;
        }

        private static void WireStaticButtons()
        {
            _selectButton.onClick.RemoveAllListeners();
            _selectButton.onClick.AddListener(OnSelect);

            // KG's prefab carries a "Create" button (open the blueprint creator). Out of scope here: disable it so the
            // browser is a pure list+place surface. Done defensively — the path may not exist on every clone.
            var create = _ui.transform.Find("Canvas/UI/Create");
            if (create != null) create.gameObject.SetActive(false);

            ResetMain();
        }

        // Force the cloned KG canvas into a known-good, visible, interactive overlay. Logs the AS-BAKED state first
        // (so the actual cause is on record), then normalizes:
        //   • render mode  → ScreenSpaceOverlay (a cloned ScreenSpaceCamera/WorldSpace canvas with a null camera draws nothing)
        //   • sortingOrder → above the Hud (a 0/low order hides the panel behind the build HUD)
        //   • CanvasScaler / GraphicRaycaster → present (a clone can drop the raycaster, leaving the panel non-interactive)
        //   • EventSystem  → present in the scene (no EventSystem ⇒ no clicks land on the list/Select)
        private static void NormalizeCanvas()
        {
            if (_canvas == null)
            {
                FiresLogger.LogError("[browser-diag] no Canvas component found on the cloned UI — it cannot render.");
                return;
            }

            var rt = _canvas.GetComponent<RectTransform>();
            FiresLogger.LogInfo(
                $"[browser-diag] AS-BAKED canvas: renderMode={_canvas.renderMode} " +
                $"sortingOrder={_canvas.sortingOrder} enabled={_canvas.enabled} worldCamera={(_canvas.worldCamera != null ? _canvas.worldCamera.name : "NULL")} " +
                $"rectSize={(rt != null ? rt.rect.size.ToString() : "no-rect")} " +
                $"hasScaler={_canvas.GetComponent<CanvasScaler>() != null} hasRaycaster={_canvas.GetComponent<GraphicRaycaster>() != null} " +
                $"EventSystem.current={(EventSystem.current != null ? EventSystem.current.name : "NULL")}");

            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.worldCamera = null;
            _canvas.sortingOrder = OverlaySortingOrder;
            _canvas.enabled = true;

            if (_canvas.GetComponent<CanvasScaler>() == null)
            {
                var scaler = _canvas.gameObject.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0.5f;
            }
            if (_canvas.GetComponent<GraphicRaycaster>() == null)
                _canvas.gameObject.AddComponent<GraphicRaycaster>();

            EnsureEventSystem();

            var rt2 = _canvas.GetComponent<RectTransform>();
            FiresLogger.LogInfo(
                $"[browser-diag] NORMALIZED canvas: renderMode={_canvas.renderMode} " +
                $"sortingOrder={_canvas.sortingOrder} enabled={_canvas.enabled} " +
                $"rectSize={(rt2 != null ? rt2.rect.size.ToString() : "no-rect")} " +
                $"EventSystem.current={(EventSystem.current != null ? EventSystem.current.name : "NULL")}");
        }

        // Valheim normally has an active EventSystem in-game (so clicks on the list/Select land), but it is not
        // guaranteed at the moment the browser first opens. Spin up a self-owned one (DontDestroyOnLoad) only when the
        // scene has none at all; Unity assigns EventSystem.current from its OnEnable, so we never poke that directly.
        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            if (UnityEngine.Object.FindObjectOfType<EventSystem>() != null) return;

            var esGo = new GameObject("FDM_EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            UnityEngine.Object.DontDestroyOnLoad(esGo);
        }

        // ── populate (SectionLibrary → list, grouped by DungeonType) ─────────────────

        /// <summary>
        /// Clear and rebuild the list from the on-disk SectionLibrary. Sections are grouped by DungeonType: one
        /// collapsible CategoryEntry per distinct type (sections with no type fall under "(no type)"), one Entry per
        /// section labelled "Name  [role NxN]". Each Entry gets a runtime click handler → ShowSection; the
        /// CategoryEntry header toggles its children. Mirrors BlueprintUI.Load/AddEntry but from our data.
        /// </summary>
        public static void Populate()
        {
            if (_ui == null) return;

            ClearList();
            ResetMain();

            List<SectionBlueprint> sections;
            try
            {
                SectionLibrary lib = FdmPaths.OpenLibrary();
                sections = lib.ListAllSections();
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"section browser: library load failed: {ex.Message}");
                sections = new List<SectionBlueprint>();
            }

            var groups = sections
                .GroupBy(s => string.IsNullOrEmpty(s.DungeonType) ? "(no type)" : s.DungeonType,
                         StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                Transform parent = AddCategory(group.Key);
                foreach (var section in group.OrderBy(s => string.IsNullOrEmpty(s.Name) ? s.SectionId : s.Name,
                                                       StringComparer.OrdinalIgnoreCase))
                    AddEntry(parent, section);
            }

            ForceLayout();
        }

        // One collapsible category header per dungeon-type, cloned from the CategoryEntry template. Returns the
        // transform new section Entries should parent under (KG nests entries inside the category for collapse).
        private static Transform AddCategory(string typeName)
        {
            GameObject cat = UnityEngine.Object.Instantiate(_categoryTemplate, _content);
            cat.name = "?Category_" + typeName;
            cat.SetActive(true);
            cat.tag = "spawned"; // start expanded so the list shows its sections on first open

            // Label the header. KG path: "Button/Name"; fall back to the first TMP we can find on the clone.
            TMP_Text label = cat.transform.Find("Button/Name")?.GetComponent<TMP_Text>()
                             ?? cat.GetComponentInChildren<TMP_Text>(true);
            if (label != null) label.text = typeName;

            // Header click toggles every child entry (children index >= 1; index 0 is the header Button row).
            Transform catTransform = cat.transform;
            Button headerButton = cat.transform.Find("Button")?.GetComponent<Button>()
                                  ?? cat.GetComponentInChildren<Button>(true);
            if (headerButton != null)
            {
                headerButton.onClick.RemoveAllListeners();
                headerButton.onClick.AddListener(() => ToggleCategory(catTransform));
            }

            return catTransform;
        }

        private static void ToggleCategory(Transform category)
        {
            bool expanded = category.gameObject.CompareTag("spawned");
            category.gameObject.tag = expanded ? "Untagged" : "spawned";
            for (int i = 1; i < category.childCount; i++)
                category.GetChild(i).gameObject.SetActive(!expanded);
            ForceLayout();
        }

        // One section row, cloned from the Entry template, parented under its category header.
        private static void AddEntry(Transform categoryParent, SectionBlueprint section)
        {
            GameObject entry = UnityEngine.Object.Instantiate(_entryTemplate, categoryParent);
            entry.SetActive(categoryParent.gameObject.CompareTag("spawned"));

            string footprint = $"{section.Footprint.x}x{section.Footprint.y}";
            string label = $"{(string.IsNullOrEmpty(section.Name) ? section.SectionId : section.Name)}  " +
                           $"<color=#9aa0a8>[{section.Role} {footprint}]</color>";
            TMP_Text nameText = entry.transform.Find("Name")?.GetComponent<TMP_Text>()
                                ?? entry.GetComponentInChildren<TMP_Text>(true);
            if (nameText != null) nameText.text = label;

            Image selection = entry.transform.Find("Selection")?.GetComponent<Image>();
            if (selection != null) selection.color = SelectionClear;

            // The imported Entry template has no game UIInputHandler, so wire a runtime Button for the click. Add one
            // if the clone doesn't already carry it; KG's left-click → ShowBlueprint is our ShowSection.
            Button button = entry.GetComponent<Button>() ?? entry.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.onClick.RemoveAllListeners();
            SectionBlueprint captured = section;
            GameObject capturedEntry = entry;
            button.onClick.AddListener(() => ShowSection(capturedEntry, captured));
        }

        // ── selection / detail panel ─────────────────────────────────────────────────

        private static void ShowSection(GameObject entry, SectionBlueprint section)
        {
            _current = section;
            _lastPressedEntry = entry;

            ClearSelections();
            HighlightEntry(entry, SelectionActive);

            if (_nameText != null) _nameText.text = string.IsNullOrEmpty(section.Name) ? section.SectionId : section.Name;
            if (_descriptionText != null)
                _descriptionText.text = BuildDescription(section);
            if (_authorText != null)
            {
                string type = string.IsNullOrEmpty(section.DungeonType) ? "(no type)" : section.DungeonType;
                _authorText.text = $"Dungeon type\n<color=green>{type}</color>";
            }
            if (_selectButtonText != null) _selectButtonText.text = "Edit in Box";

            _main.gameObject.SetActive(true);
            ForceLayout();
        }

        private static string BuildDescription(SectionBlueprint s)
        {
            string body = string.IsNullOrWhiteSpace(s.Description) ? "" : s.Description + "\n\n";
            return body +
                   $"<color=#9aa0a8>Role:</color> {s.Role}\n" +
                   $"<color=#9aa0a8>Footprint:</color> {s.Footprint.x} x {s.Footprint.y}  (stories {s.Stories})\n" +
                   $"<color=#9aa0a8>Objects:</color> {s.Objects.Count}\n" +
                   $"<color=#9aa0a8>Id:</color> {s.SectionId}";
        }

        // ── place (Select → SectionPlacer, the firesdungeon_place path) ──────────────

        private static void OnSelect()
        {
            if (_current == null) return;

            string sectionId = _current.SectionId;
            Hide();

            // EDIT-in-box (not final place): spawn the section as an editable footprint box in front of the player so
            // an admin can move pieces / retag and re-save. Admin-gated but NOT host-only (works from a dedi client).
            string result = SectionEditSpawner.SpawnForEditById(sectionId);
            FiresLogger.LogInfo($"section browser edit-spawn: {result}");
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "[FiresDungeonMaster] " + result);
        }

        // ── show / hide ──────────────────────────────────────────────────────────────

        /// <summary>Open the browser (lazy-Init), rebuilding the list from the current library each time.</summary>
        public static void Show()
        {
            if (IsClientOnly()) return;
            if (!Init()) return;

            // Re-assert the overlay every open: a relogin can rebuild the Hud / EventSystem under us, and Populate
            // can run before the canvas settles. Cheap and idempotent — keeps the panel on top after a scene swap.
            NormalizeCanvas();

            Populate();
            _ui.SetActive(true);

            // Modal input handling — the SAME pattern the leaderboard / ConfigPanel use: InputBlock blocks player
            // input + PINS the camera, and we free the OS cursor. Without this the browser was open but camera look +
            // movement kept responding (the reported bug). Update() re-forces the cursor each frame while visible.
            if (!_blocked) { FiresCore.UI.InputBlock.Block(true); _blocked = true; }
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            var rt = _canvas != null ? _canvas.GetComponent<RectTransform>() : null;
            FiresLogger.LogInfo(
                $"[browser-diag] Show(): ui.activeSelf={_ui.activeSelf} " +
                $"canvas.enabled={(_canvas != null && _canvas.enabled)} sortingOrder={(_canvas != null ? _canvas.sortingOrder : -1)} " +
                $"rectSize={(rt != null ? rt.rect.size.ToString() : "no-rect")}");
        }

        public static void Hide()
        {
            if (_ui != null) _ui.SetActive(false);
            if (_blocked) { FiresCore.UI.InputBlock.Block(false); _blocked = false; }
        }

        /// <summary>Toggle open/closed — the command/hotkey entry point.</summary>
        public static void Toggle()
        {
            if (IsClientOnly()) return;
            if (IsVisible) Hide();
            else Show();
        }

        /// <summary>Esc closes the browser when it's open. Called from the mod's Hud update hook.</summary>
        public static void Update()
        {
            if (!IsVisible) return;
            // Re-force the freed cursor each frame (the Hud/GameCamera try to re-lock it), matching ConfigPanel.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            if (Input.GetKeyDown(KeyCode.Escape)) Hide();
        }

        // ── helpers ──────────────────────────────────────────────────────────────────

        private static void ResetMain()
        {
            _current = null;
            _lastPressedEntry = null;
            if (_main != null) _main.gameObject.SetActive(false);
            if (_nameText != null) _nameText.text = "";
            if (_descriptionText != null) _descriptionText.text = "";
            if (_authorText != null) _authorText.text = "";
            ClearSelections();
        }

        // Remove every cloned category + entry, leaving the two hidden templates (Entry, CategoryEntry) intact.
        private static void ClearList()
        {
            if (_content == null) return;
            var doomed = new List<GameObject>();
            foreach (Transform child in _content)
            {
                if (child.gameObject == _entryTemplate || child.gameObject == _categoryTemplate) continue;
                doomed.Add(child.gameObject);
            }
            foreach (var go in doomed) UnityEngine.Object.Destroy(go);
        }

        private static void ClearSelections()
        {
            if (_content == null) return;
            foreach (var img in _content.GetComponentsInChildren<Image>(true))
                if (img.gameObject.name == "Selection") img.color = SelectionClear;
        }

        private static void HighlightEntry(GameObject entry, Color color)
        {
            if (entry == null) return;
            var sel = entry.transform.Find("Selection")?.GetComponent<Image>();
            if (sel != null) sel.color = color;
        }

        private static void ForceLayout()
        {
            if (_ui == null) return;
            var fitters = _ui.GetComponentsInChildren<ContentSizeFitter>(true);
            Canvas.ForceUpdateCanvases();
            foreach (var f in fitters) { f.enabled = false; f.enabled = true; }
            Canvas.ForceUpdateCanvases();
        }

        /// <summary>
        /// Client-only hotkey + Esc-close driver. F8-free default bind is Shift+B (open/close the browser); Esc closes
        /// it while open. Skipped on a dedicated server (the Hud patch is in the dedi-skip set, and the body guards on
        /// IsClientOnly anyway). Hooking Hud.Update keeps us off a per-frame MonoBehaviour of our own.
        /// </summary>
        [HarmonyPatch(typeof(Hud), "Update")]
        internal static class Hud_Update_Hotkey_Patch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (IsClientOnly()) return;
                if (Player.m_localPlayer == null) return;
                if (Console.IsVisible() || (Chat.instance != null && Chat.instance.HasFocus())) return;
                // Raw Input bypasses the Core ZInput/KeyboardShortcut gate, so a 'B' typed into any Fires text field
                // (e.g. a door's Display Name) would fire this. Honour the text-capture flag explicitly.
                if (FiresCore.Input.FiresInputBlock.IsCapturing) return;

                // Shift+B now opens the BAKED blueprint bench (this old KG-clone browser is superseded).
                // ADMIN ONLY — the bench is dungeon-authoring tooling (save/place sections, generate dungeons).
                if (Input.GetKey(KeyCode.LeftShift) && Input.GetKeyDown(KeyCode.B))
                {
                    if (!FiresDungeonMaster.Editor.SectionFootprintController.IsAdmin()) return;
                    BlueprintBenchPanel.Toggle();
                }
            }
        }
    }
}
