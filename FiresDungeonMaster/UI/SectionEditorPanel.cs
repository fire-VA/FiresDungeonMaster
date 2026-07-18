using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using TMPro;
using FiresCore.UI;
using FiresCore.UI.ContextMenu;
using FiresDungeonMaster.Editor;
using FiresDungeonMaster.Sections;

namespace FiresDungeonMaster.UI
{
    using MarkerHit = SectionFootprintController.MarkerHit;
    using EdgeTag = SectionFootprintController.EdgeTag;
    using Colors = FiresCore.UI.UIFontConfig.Colors;

    /// <summary>
    /// SectionEditorPanel — the dungeon-section editor window opened by Alt+Shift right-clicking a footprint sphere
    /// (the target is CLAIMED by SectionFootprintContextProvider so the shared companion menu never shows). Styled
    /// to match the Fires parchment UIs: tan ParchmentPanel face + dark outline, dark-brown ParchmentButtons with
    /// cream ink, and the family Averia Sans font (UIFontConfig.FontStyle.AveriaLibre) on every label. Draggable by
    /// the title bar + resizable by the bottom-right grip, with a scrollable body — the same affordances as the
    /// Core ConfigPanel / book UIs.
    ///
    /// Modality: standalone top-level ScreenSpaceOverlay canvas (never under the Hud), InputBlock (camera pin +
    /// input suppression), per-frame cursor-free (Update + OnGUI), crosshair raycast suppressed, and
    /// FiresContextMenu.SuppressDriver so the context driver stands down and hands off (not toggles off) the shared
    /// boolean InputBlock — this panel owns it and releases it on close. Client-only; rebuilds its body after every
    /// edit so the active choice highlights live and the center readout stays current.
    /// </summary>
    public class SectionEditorPanel : MonoBehaviour
    {
        private const int OverlaySortingOrder = 5000;
        private const float TitleBarH = 46f;
        private static readonly Vector2 MinSize = new Vector2(400f, 260f);

        // Parchment palette (from the shared family tokens) so this matches the book / config UIs.
        private static readonly Color PanelFace   = Colors.ParchmentPanel;                 // light tan
        private static readonly Color TitleFace   = Colors.ParchmentHeader;                // darker brown strip
        private static readonly Color Outline     = Colors.ParchmentOutline;
        private static readonly Color Ink         = Colors.ParchmentInk;                   // dark text on tan
        private static readonly Color InkLabel    = Colors.ParchmentLabel;                 // muted dark text on tan
        private static readonly Color Cream       = Colors.ParchmentButtonInk;             // light text on brown
        private static readonly Color TitleInk    = Colors.BrightGold;
        private static readonly Color ReadoutStrip= new Color(0.72f, 0.63f, 0.45f, 0.95f); // slightly darker tan band
        private static readonly Color ActiveFace  = Colors.ParchmentButtonHover;           // highlighted current choice
        private static readonly Color GripFace    = new Color(0.30f, 0.20f, 0.09f, 0.85f);

        // Edge choices the window offers (Left/Right hints stay Shift+Scroll-only, matching the old menu).
        private static readonly EdgeTag[] EdgeChoices =
            { EdgeTag.Wall, EdgeTag.Doorway, EdgeTag.Open, EdgeTag.Window, EdgeTag.Back };

        private static SectionEditorPanel _instance;

        private GameObject _root;
        private RectTransform _dialog;
        private RectTransform _content;
        private SectionFootprintController _fp;
        private MarkerHit _marker;
        private bool _blockedByUs;
        private readonly List<Graphic> _crosshair = new List<Graphic>();

        private static bool IsClientOnly() => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

        public static bool IsOpen => _instance != null && _instance._root != null && _instance._root.activeSelf;

        /// <summary>Rebuild the open editor's body so a change made by a SEPARATE modal (e.g. the dungeon-type input
        /// writing a new type) shows immediately in the readout. No-op when the editor is closed.</summary>
        public static void RefreshIfOpen()
        {
            if (_instance != null && IsOpen && _instance._fp != null) _instance.BuildDialog();
        }

        public static void EnsureHost()
        {
            if (_instance != null) return;
            var go = new GameObject("FDM_SectionEditorHost");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<SectionEditorPanel>();
            FiresCore.Bridge.ModUiRegistry.Register("FDM_SectionEditor", () => IsOpen, () => _instance?.Hide(), () => _instance?._root);
        }

        /// <summary>Open (or retarget) the editor for one footprint sphere. Idempotent — a second sphere retargets.</summary>
        public static void Open(SectionFootprintController fp, MarkerHit marker)
        {
            if (IsClientOnly() || fp == null) return;
            EnsureHost();
            _instance._Open(fp, marker);
        }

        private void _Open(SectionFootprintController fp, MarkerHit marker)
        {
            _fp = fp;
            _marker = marker;
            if (_root == null) BuildCanvas();
            _root.SetActive(true);
            _root.transform.SetAsLastSibling();
            BuildDialog();

            FiresContextMenu.SuppressDriver = true;   // context driver stands down; we own modality now
            if (!InputBlock.IsBlocked) InputBlock.Block(true);
            _blockedByUs = true;                       // driver handed us the (unowned) block via SuppressDriver
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            try { CrosshairRaycast.Suppress(_crosshair); } catch { }
        }

        public void Hide()
        {
            if (_root != null) _root.SetActive(false);
            if (_blockedByUs) { InputBlock.Block(false); _blockedByUs = false; }
            FiresContextMenu.SuppressDriver = false;
            try { CrosshairRaycast.Restore(_crosshair); } catch { }
            _fp = null;
        }

        private void Update()
        {
            if (!IsOpen) return;
            if (_fp == null) { Hide(); return; }        // footprint streamed out
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)) Hide();
        }

        private void OnGUI()
        {
            if (!IsOpen) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            try { CrosshairRaycast.Reassert(_crosshair); } catch { }
        }

        // ---------------------------------------------------------------- scaffold
        private void BuildCanvas()
        {
            _root = new GameObject("FDM_SectionEditorRoot", typeof(RectTransform));
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = OverlaySortingOrder;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _root.AddComponent<GraphicRaycaster>();
            var rt = _root.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;

            var back = _root.AddComponent<Image>();
            back.color = new Color(0f, 0f, 0f, 0.5f);
            var backBtn = _root.AddComponent<Button>();
            backBtn.targetGraphic = back;
            var bc = backBtn.colors; bc.normalColor = bc.highlightedColor = bc.pressedColor = bc.selectedColor = back.color; backBtn.colors = bc;
            backBtn.onClick.AddListener(Hide);
        }

        private void BuildDialog()
        {
            bool center = _marker == MarkerHit.Center;
            Vector2 size = center ? new Vector2(540f, 700f) : new Vector2(520f, 470f);
            Vector2 keepPos = _dialog != null ? _dialog.anchoredPosition : new Vector2((1920f - size.x) / 2f, -(1080f - size.y) / 2f);
            if (_dialog != null) { Destroy(_dialog.gameObject); _dialog = null; }

            _dialog = NewRect("Dialog", _root.transform);
            _dialog.anchorMin = _dialog.anchorMax = new Vector2(0f, 1f);   // top-left pivot so the grip grows down-right
            _dialog.pivot = new Vector2(0f, 1f);
            _dialog.sizeDelta = size;
            _dialog.anchoredPosition = keepPos;
            var img = _dialog.gameObject.AddComponent<Image>();
            img.color = PanelFace; img.raycastTarget = true;
            _dialog.gameObject.AddComponent<Button>().targetGraphic = img;   // absorb clicks on empty dialog area
            var ol = _dialog.gameObject.AddComponent<Outline>();
            ol.effectColor = Outline; ol.effectDistance = new Vector2(2.5f, -2.5f);

            BuildTitleBar(_dialog, center);
            BuildBodyScroll(_dialog);
            BuildResizeGrip(_dialog);

            if (center) BuildCenterBody(_content); else BuildEdgeBody(_content, _marker);

            ApplyAveria(_dialog.gameObject);   // family font on every label (overrides CreateButton's default)
        }

        private void BuildTitleBar(RectTransform dialog, bool center)
        {
            var bar = NewRect("TitleBar", dialog);
            bar.anchorMin = new Vector2(0f, 1f); bar.anchorMax = new Vector2(1f, 1f); bar.pivot = new Vector2(0.5f, 1f);
            bar.sizeDelta = new Vector2(0f, TitleBarH); bar.anchoredPosition = Vector2.zero;
            bar.gameObject.AddComponent<Image>().color = TitleFace;
            bar.gameObject.AddComponent<DragMove>().Target = dialog;   // drag the bar to move the window

            string titleText = center ? "Dungeon Section  -  Center" : $"Section Edge  -  {FaceWord(_marker)}";
            var title = Label(bar, titleText, 21f, TitleInk, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            title.anchorMin = new Vector2(0f, 0f); title.anchorMax = new Vector2(1f, 1f); title.pivot = new Vector2(0f, 0.5f);
            title.offsetMin = new Vector2(18f, 0f); title.offsetMax = new Vector2(-52f, 0f);

            var close = NewRect("Close", bar);
            close.anchorMin = new Vector2(1f, 0.5f); close.anchorMax = new Vector2(1f, 0.5f); close.pivot = new Vector2(1f, 0.5f);
            close.anchoredPosition = new Vector2(-8f, 0f); close.sizeDelta = new Vector2(32f, 32f);
            var x = UIBuilderHelper.CreateParchmentButton(close, "X", Vector2.zero, Vector2.one, Hide);
            var xl = x.GetComponentInChildren<TextMeshProUGUI>();
            if (xl != null) { xl.enableAutoSizing = false; xl.fontSize = 16f; xl.fontStyle = FontStyles.Bold; }
        }

        private void BuildBodyScroll(RectTransform dialog)
        {
            var area = NewRect("Body", dialog);
            area.anchorMin = Vector2.zero; area.anchorMax = Vector2.one;
            area.offsetMin = new Vector2(0f, 0f); area.offsetMax = new Vector2(0f, -TitleBarH);

            var vp = NewRect("Viewport", area);
            vp.anchorMin = Vector2.zero; vp.anchorMax = Vector2.one; vp.offsetMin = new Vector2(4f, 4f); vp.offsetMax = new Vector2(-4f, -2f);
            vp.gameObject.AddComponent<RectMask2D>();
            var vpImg = vp.gameObject.AddComponent<Image>(); vpImg.color = new Color(0f, 0f, 0f, 0f); vpImg.raycastTarget = true;

            _content = NewRect("Content", vp);
            _content.anchorMin = new Vector2(0f, 1f); _content.anchorMax = new Vector2(1f, 1f); _content.pivot = new Vector2(0.5f, 1f);
            _content.sizeDelta = Vector2.zero;
            var vlg = _content.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(16, 16, 12, 14); vlg.spacing = 7;
            vlg.childControlWidth = vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            _content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = area.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false; scroll.vertical = true; scroll.viewport = vp; scroll.content = _content;
            scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 26f;
        }

        private void BuildResizeGrip(RectTransform dialog)
        {
            var grip = NewRect("ResizeGrip", dialog);
            grip.anchorMin = grip.anchorMax = new Vector2(1f, 0f); grip.pivot = new Vector2(1f, 0f);
            grip.anchoredPosition = Vector2.zero; grip.sizeDelta = new Vector2(20f, 20f);
            grip.gameObject.AddComponent<Image>().color = GripFace;
            var dr = grip.gameObject.AddComponent<DragResize>(); dr.Target = dialog; dr.MinSize = MinSize;
            var g = Label(grip, "//", 12f, Cream, TextAlignmentOptions.Center, FontStyles.Bold);
            g.anchorMin = Vector2.zero; g.anchorMax = Vector2.one; g.offsetMin = g.offsetMax = Vector2.zero;
        }

        // ---------------------------------------------------------------- edge body
        private void BuildEdgeBody(RectTransform body, MarkerHit face)
        {
            EdgeTag cur = _fp.EdgeTagOf(face);
            AddReadout(body, $"Current:  <b>{EdgeLabel(cur)}</b>");
            AddNote(body, "Sets how this face generates. A wall / door / window becomes the build guide for that side.");

            foreach (var tag in EdgeChoices)
            {
                var t = tag;
                bool active = cur == t;
                AddChoiceButton(body, EdgeLabel(t), EdgeBlurb(t), active, () => { _fp.SetEdgeTag(face, t); BuildDialog(); });
            }
        }

        // ---------------------------------------------------------------- center body
        private void BuildCenterBody(RectTransform body)
        {
            AddReadout(body,
                $"Role <b>{_fp.RoleDisplayName()}</b>     Type <b>{_fp.DungeonTypeDisplayName()}</b>     <b>{_fp.Stories}</b> story");

            AddActionButton(body, $"Dungeon Type:  {_fp.DungeonTypeDisplayName()}   (next)", () => { _fp.CycleDungeonType(+1); BuildDialog(); });
            AddActionButton(body, "Set New Type...", () => _fp.OpenDungeonTypeInput());
            AddActionButton(body, "Height  +1 story", () => { _fp.AdjustStories(+1); BuildDialog(); });
            AddActionButton(body, "Height  -1 story", () => { _fp.AdjustStories(-1); BuildDialog(); });
            AddActionButton(body, "Save Blueprint", () => _fp.SaveBlueprint());

            AddSectionLabel(body, "Module Role");
            string curRole = _fp.RoleDisplayName();
            foreach (SectionRole role in Enum.GetValues(typeof(SectionRole)))
            {
                var r = role;
                bool active = string.Equals(curRole, role.ToString(), StringComparison.Ordinal);
                AddChoiceButton(body, role.ToString(), null, active, () => { _fp.SetRole(r); BuildDialog(); });
            }
        }

        // ---------------------------------------------------------------- widgets
        private void AddReadout(RectTransform parent, string richText)
        {
            var go = NewRect("Readout", parent);
            go.gameObject.AddComponent<LayoutElement>().minHeight = 32f;
            go.gameObject.AddComponent<Image>().color = ReadoutStrip;
            Stretch(Label(go, richText, 15.5f, Ink, TextAlignmentOptions.Center, FontStyles.Bold), 8, 0, 8, 0);
        }

        private void AddNote(RectTransform parent, string text)
        {
            var go = NewRect("Note", parent);
            go.gameObject.AddComponent<LayoutElement>().minHeight = 36f;
            Stretch(Label(go, text, 12.5f, InkLabel, TextAlignmentOptions.TopLeft, FontStyles.Italic, wrap: true), 2, 2, 2, 2);
        }

        private void AddSectionLabel(RectTransform parent, string text)
        {
            var go = NewRect("SectionLabel", parent);
            go.gameObject.AddComponent<LayoutElement>().minHeight = 26f;
            Stretch(Label(go, text, 13.5f, InkLabel, TextAlignmentOptions.BottomLeft, FontStyles.Bold), 2, 6, 2, 0);
        }

        private void AddActionButton(RectTransform parent, string text, UnityEngine.Events.UnityAction onClick)
        {
            var slot = NewRect("Action", parent);
            slot.gameObject.AddComponent<LayoutElement>().minHeight = 40f;
            var btn = UIBuilderHelper.CreateParchmentButton(slot, text, Vector2.zero, Vector2.one, onClick);
            StyleButtonLabel(btn, false, TextAlignmentOptions.Center);
        }

        // Choice button that highlights when it is the current selection; optional right-aligned blurb.
        private void AddChoiceButton(RectTransform parent, string text, string blurb, bool active, UnityEngine.Events.UnityAction onClick)
        {
            var slot = NewRect("Choice", parent);
            slot.gameObject.AddComponent<LayoutElement>().minHeight = 42f;
            var btn = UIBuilderHelper.CreateParchmentButton(slot, text, Vector2.zero, Vector2.one, active ? (UnityEngine.Events.UnityAction)null : onClick);
            if (active)
            {
                btn.GetComponent<Image>().color = ActiveFace;
                var cols = btn.colors; cols.normalColor = cols.selectedColor = ActiveFace; btn.colors = cols;
            }
            StyleButtonLabel(btn, active, TextAlignmentOptions.MidlineLeft);
            var lbl = btn.GetComponentInChildren<TextMeshProUGUI>();
            if (lbl != null) lbl.margin = new Vector4(16f, 0f, 12f, 0f);

            if (!string.IsNullOrEmpty(blurb))
            {
                var b = Label(btn.transform, blurb, 12f, new Color(Cream.r, Cream.g, Cream.b, active ? 1f : 0.72f), TextAlignmentOptions.MidlineRight, FontStyles.Italic);
                b.anchorMin = Vector2.zero; b.anchorMax = Vector2.one; b.offsetMin = new Vector2(8f, 0f); b.offsetMax = new Vector2(-14f, 0f);
                b.GetComponent<TextMeshProUGUI>().raycastTarget = false;
            }
        }

        private static void StyleButtonLabel(Button btn, bool active, TextAlignmentOptions align)
        {
            var lbl = btn.GetComponentInChildren<TextMeshProUGUI>();
            if (lbl == null) return;
            lbl.enableAutoSizing = false;
            lbl.fontSize = 15f;
            lbl.color = active ? Colors.BrightGold : Cream;
            lbl.fontStyle = active ? FontStyles.Bold : FontStyles.Normal;
            lbl.alignment = align;
        }

        // ---------------------------------------------------------------- font
        private static void ApplyAveria(GameObject root)
        {
            var font = UIFontConfig.GetFont(UIFontConfig.FontStyle.AveriaLibre);
            if (font == null) return;
            foreach (var tmp in root.GetComponentsInChildren<TMP_Text>(true)) tmp.font = font;
        }

        // ---------------------------------------------------------------- labels / rects
        private static string EdgeLabel(EdgeTag t)
        {
            switch (t)
            {
                case EdgeTag.Wall:    return "Wall";
                case EdgeTag.Doorway: return "Doorway";
                case EdgeTag.Open:    return "Open";
                case EdgeTag.Window:  return "Window";
                case EdgeTag.Back:    return "Back Wall";
                default:              return t.ToString();
            }
        }

        private static string EdgeBlurb(EdgeTag t)
        {
            switch (t)
            {
                case EdgeTag.Wall:    return "solid";
                case EdgeTag.Doorway: return "door opening";
                case EdgeTag.Open:    return "full arch";
                case EdgeTag.Window:  return "window";
                case EdgeTag.Back:    return "dead-end";
                default:              return "";
            }
        }

        private static string FaceWord(MarkerHit m)
        {
            switch (m)
            {
                case MarkerHit.North: return "North";
                case MarkerHit.East:  return "East";
                case MarkerHit.South: return "South";
                case MarkerHit.West:  return "West";
                default:              return m.ToString();
            }
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.GetComponent<RectTransform>();
        }

        private static void Stretch(RectTransform rt, float l = 0, float t = 0, float r = 0, float b = 0)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(l, b); rt.offsetMax = new Vector2(-r, -t);
        }

        private static RectTransform Label(Transform parent, string text, float size, Color color,
            TextAlignmentOptions align, FontStyles style = FontStyles.Normal, bool wrap = false)
        {
            var rt = NewRect("Text", parent);
            var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
            tmp.text = text; tmp.fontSize = size; tmp.color = color; tmp.alignment = align; tmp.fontStyle = style;
            tmp.raycastTarget = false; tmp.richText = true;
            tmp.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            if (!wrap) tmp.overflowMode = TextOverflowModes.Ellipsis;
            return rt;
        }
    }
}
