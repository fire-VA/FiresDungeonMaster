using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using TMPro;
using HarmonyLib;
using FiresCore.UI;
using FiresCore.UI.ContextMenu;

namespace FiresDungeonMaster.UI
{
    /// <summary>
    /// LockpickPanel — the steampunk lock-picking minigame for Lock=Key doors. Art (user-authored, embedded PNGs):
    /// a static HOUSING (5 empty slots), one PIN sprite reused 5×, and the draggable PICK rod. Mechanic: hold LMB and
    /// drag the pick to line it up under a pin (nearest slot = active), drag UP to raise that pin, tap E to set it when
    /// it rattles in its sweet spot; a wrong tap drops it. All 5 set → unlocked; timer runs out → fail. A SNAP costs a
    /// lockpick (onBreak fires per snap) but does NOT close the panel — with a spare you draw a fresh pick and keep
    /// going; only running out of picks closes it. The snap's jolt can POP seated pins back out — each rolls a retain
    /// chance that Lockpicking raises (skill protects progress). Standalone ScreenSpaceOverlay modal (InputBlock +
    /// FiresInputBlock so nothing leaks), client-only.
    ///
    /// Slot X's / pin Y range / scales are TUNING constants (see LockpickController) dialled against a render to sit
    /// the pins in the housing slots (the art was authored at a different scale — we scale + place at runtime).
    /// </summary>
    public static class LockpickPanel
    {
        private static GameObject _canvasGo;
        private static bool _blocked;
        private static Sprite _housing, _pin, _pick;
        private static TextMeshProUGUI _countText;   // pick-count readout — refreshed when a snap consumes a pick
        private static string _countPrefix = "";
        private static readonly object PickToken = new object();

        public static bool IsOpen => _canvasGo != null;
        private static bool IsClientOnly() => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

        public static void Open(string quality, int pinCount, float breakChance, float skillFactor, Action onSuccess, Action onFail, Action onBreak = null)
        {
            if (IsClientOnly()) return;
            Close();
            if (!LoadSprites()) { onFail?.Invoke(); return; }
            Build(quality, Mathf.Clamp(pinCount, 1, 5), breakChance, Mathf.Clamp01(skillFactor), onSuccess, onFail, onBreak);
        }

        private static bool LoadSprites()
        {
            _housing = _housing ?? LoadSprite("FiresLockHousing.png");
            _pin = _pin ?? LoadSprite("FiresLockPin.png");
            _pick = _pick ?? LoadSprite("FiresLockPick.png");
            return _housing != null && _pin != null && _pick != null;
        }

        private static Sprite LoadSprite(string endsWith)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string res = null;
                foreach (var n in asm.GetManifestResourceNames()) if (n.EndsWith(endsWith, StringComparison.OrdinalIgnoreCase)) { res = n; break; }
                if (res == null) { FiresLogger.LogError($"lockpick sprite '{endsWith}' not embedded."); return null; }
                using var s = asm.GetManifestResourceStream(res);
                using var ms = new System.IO.MemoryStream(); s.CopyTo(ms);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!LoadImageReflect(tex, ms.ToArray())) return null;
                tex.wrapMode = TextureWrapMode.Clamp; tex.filterMode = FilterMode.Bilinear;
                return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            catch (Exception ex) { FiresLogger.LogError($"lockpick sprite load failed: {ex.Message}"); return null; }
        }

        // Texture2D.LoadImage lives in UnityEngine.ImageConversionModule, which drags in netstandard 2.1 and breaks the
        // net48 build if referenced directly (same trap as the AudioModule). Drive it by reflection instead — the module
        // is loaded at runtime, so the type resolves then.
        private static MethodInfo _loadImage;
        private static bool _loadImageResolved;
        private static bool LoadImageReflect(Texture2D tex, byte[] data)
        {
            try
            {
                if (!_loadImageResolved)
                {
                    _loadImageResolved = true;
                    Type t = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
                    if (t == null)
                        foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { t = a.GetType("UnityEngine.ImageConversion"); if (t != null) break; }
                    if (t != null)
                        _loadImage = t.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) })
                                     ?? t.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });
                }
                if (_loadImage == null) { FiresLogger.LogError("ImageConversion.LoadImage not found"); return false; }
                var args = _loadImage.GetParameters().Length == 3 ? new object[] { tex, data, false } : new object[] { tex, data };
                var r = _loadImage.Invoke(null, args);
                return !(r is bool b) || b;
            }
            catch (Exception ex) { FiresLogger.LogError($"LoadImage reflect failed: {ex.Message}"); return false; }
        }

        private static void Build(string quality, int pinCount, float breakChance, float skillFactor, Action onSuccess, Action onFail, Action onBreak)
        {
            _canvasGo = new GameObject("FDM_LockpickPanel");
            UnityEngine.Object.DontDestroyOnLoad(_canvasGo);
            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 6600;
            var scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _canvasGo.AddComponent<GraphicRaycaster>();

            // Dim backdrop.
            var dim = NewImage(_canvasGo.transform, "Dim", new Color(0f, 0f, 0f, 0.6f));
            var drt = dim.rectTransform; drt.anchorMin = Vector2.zero; drt.anchorMax = Vector2.one; drt.offsetMin = drt.offsetMax = Vector2.zero;

            // Assembly container (centred). Render order INSIDE it: pins (behind) → housing → pick (front).
            // The hollow-cylinder housing occludes the rising springs (compression illusion) while its open slots reveal
            // the bolts. Pins are siblings drawn BEFORE the housing, so the housing image sits on top of them.
            var assembly = new GameObject("LockAssembly", typeof(RectTransform)).GetComponent<RectTransform>();
            assembly.SetParent(_canvasGo.transform, false);
            assembly.anchorMin = assembly.anchorMax = new Vector2(0.5f, 0.5f); assembly.pivot = new Vector2(0.5f, 0.5f);
            assembly.sizeDelta = new Vector2(LockpickController.HousingW, LockpickController.HousingH);
            assembly.anchoredPosition = Vector2.zero;

            var housing = NewImage(assembly, "Housing", Color.white);
            housing.sprite = _housing; housing.preserveAspect = true; housing.raycastTarget = false;
            var hrt = housing.rectTransform;
            hrt.anchorMin = hrt.anchorMax = new Vector2(0.5f, 0.5f); hrt.pivot = new Vector2(0.5f, 0.5f);
            hrt.sizeDelta = new Vector2(LockpickController.HousingW, LockpickController.HousingH);
            hrt.anchoredPosition = Vector2.zero;

            var ctrl = _canvasGo.AddComponent<LockpickController>();
            ctrl.Init(assembly, _pin, _pick, quality, pinCount, breakChance, skillFactor, onSuccess, onFail, onBreak);

            // Title + timer + hint (children of the canvas, above the lock).
            var title = NewText(_canvasGo.transform, "Title", $"Picking Lock — {quality}", 26f, new Color(0.95f, 0.86f, 0.6f, 1f));
            var trt = title.rectTransform; trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 0.5f); trt.pivot = new Vector2(0.5f, 0.5f);
            trt.sizeDelta = new Vector2(700f, 40f); trt.anchoredPosition = new Vector2(0f, LockpickController.HousingH * 0.5f + 54f);

            var timerBg = NewImage(_canvasGo.transform, "TimerBg", new Color(0f, 0f, 0f, 0.5f));
            var tbrt = timerBg.rectTransform; tbrt.anchorMin = tbrt.anchorMax = new Vector2(0.5f, 0.5f); tbrt.pivot = new Vector2(0.5f, 0.5f);
            tbrt.sizeDelta = new Vector2(420f, 18f); tbrt.anchoredPosition = new Vector2(0f, LockpickController.HousingH * 0.5f + 24f);
            var timerFill = NewImage(timerBg.transform, "Fill", new Color(0.3f, 0.85f, 0.3f, 1f));
            var tfrt = timerFill.rectTransform; tfrt.anchorMin = new Vector2(0f, 0f); tfrt.anchorMax = new Vector2(1f, 1f); tfrt.offsetMin = tfrt.offsetMax = Vector2.zero;
            // Image IGNORES fillAmount while its sprite is null (OnPopulateMesh falls back to a plain full quad), so
            // the fill needs a sprite or the bar never visibly drains.
            timerFill.sprite = WhiteSprite();
            timerFill.type = Image.Type.Filled; timerFill.fillMethod = Image.FillMethod.Horizontal; timerFill.fillOrigin = 0; timerFill.fillAmount = 1f;
            ctrl.TimerFill = timerFill;

            // Info row under the lock: Lockpicking level (left) and remaining picks (right) — what the player is
            // working with and what a snap costs them. The count refreshes when a snap consumes a pick.
            float infoY = -LockpickController.HousingH * 0.5f - 24f;
            var skill = NewText(_canvasGo.transform, "SkillInfo", $"Lockpicking  Lv {Mathf.FloorToInt(Editor.LockpickSkill.LocalLevel())}", 16f, new Color(0.72f, 0.85f, 0.72f, 1f));
            skill.alignment = TextAlignmentOptions.MidlineLeft;
            var skrt = skill.rectTransform; skrt.anchorMin = skrt.anchorMax = new Vector2(0.5f, 0.5f); skrt.pivot = new Vector2(0.5f, 0.5f);
            skrt.sizeDelta = new Vector2(LockpickController.HousingW, 26f); skrt.anchoredPosition = new Vector2(0f, infoY);

            var pickIcon = LockpickItemIcon();
            if (pickIcon != null)
            {
                var im = NewImage(_canvasGo.transform, "PickIcon", Color.white);
                im.sprite = pickIcon; im.preserveAspect = true; im.raycastTarget = false;
                var irt = im.rectTransform; irt.anchorMin = irt.anchorMax = new Vector2(0.5f, 0.5f); irt.pivot = new Vector2(0.5f, 0.5f);
                irt.sizeDelta = new Vector2(30f, 30f); irt.anchoredPosition = new Vector2(LockpickController.HousingW * 0.5f - 88f, infoY);
            }
            _countPrefix = pickIcon != null ? "× " : "Picks × ";
            _countText = NewText(_canvasGo.transform, "PickCount", _countPrefix + CountLockpicks(), 16f, new Color(0.9f, 0.88f, 0.8f, 1f));
            _countText.alignment = TextAlignmentOptions.MidlineLeft;
            var cnrt = _countText.rectTransform; cnrt.anchorMin = cnrt.anchorMax = new Vector2(0.5f, 0.5f); cnrt.pivot = new Vector2(0.5f, 0.5f);
            cnrt.sizeDelta = new Vector2(140f, 26f); cnrt.anchoredPosition = new Vector2(LockpickController.HousingW * 0.5f + (pickIcon != null ? 0f : -20f), infoY);

            var hint = NewText(_canvasGo.transform, "Hint", "Move the pick under a pin · hold [LMB] + drag UP to raise it · tap [E] to set · [Esc] give up", 15f, new Color(0.8f, 0.82f, 0.85f, 1f));
            var hrt2 = hint.rectTransform; hrt2.anchorMin = hrt2.anchorMax = new Vector2(0.5f, 0.5f); hrt2.pivot = new Vector2(0.5f, 0.5f);
            hrt2.sizeDelta = new Vector2(900f, 30f); hrt2.anchoredPosition = new Vector2(0f, -LockpickController.HousingH * 0.5f - 54f);

            // Full-screen flash overlay, last sibling so it renders over everything: gold on unlock, red on a snap.
            var flash = NewImage(_canvasGo.transform, "Flash", new Color(1f, 1f, 1f, 0f));
            flash.raycastTarget = false;
            var flrt = flash.rectTransform; flrt.anchorMin = Vector2.zero; flrt.anchorMax = Vector2.one; flrt.offsetMin = flrt.offsetMax = Vector2.zero;
            ctrl.Flash = flash;

            var averia = UIFontConfig.GetFont(UIFontConfig.FontStyle.AveriaLibre);
            if (averia != null) foreach (var t in _canvasGo.GetComponentsInChildren<TMP_Text>(true)) t.font = averia;

            FiresContextMenu.SuppressDriver = true;
            if (!InputBlock.IsBlocked) InputBlock.Block(true);
            _blocked = true;
            FiresCore.Input.FiresInputBlock.Acquire(PickToken);
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }

        internal static void Finish(bool won, Action onSuccess, Action onFail)
        {
            Close();
            if (won) onSuccess?.Invoke(); else onFail?.Invoke();
        }

        public static void Close()
        {
            _countText = null;
            FiresContextMenu.SuppressDriver = false;
            FiresCore.Input.FiresInputBlock.Release(PickToken);
            if (_blocked) { InputBlock.Block(false); _blocked = false; }
            if (_canvasGo != null) { UnityEngine.Object.Destroy(_canvasGo); _canvasGo = null; }
        }

        internal static void RefreshPickCount()
        {
            if (_countText != null) _countText.text = _countPrefix + CountLockpicks();
        }

        // Total FiresLockpick items (stacks summed) in the local player's inventory — matched by drop-prefab name,
        // same as EntryDoorController's gate check.
        internal static int CountLockpicks()
        {
            var inv = Player.m_localPlayer?.GetInventory();
            if (inv == null) return 0;
            int total = 0;
            var all = inv.GetAllItems();
            for (int i = 0; i < all.Count; i++)
            {
                var it = all[i];
                if (it != null && it.m_dropPrefab != null && it.m_dropPrefab.name == "FiresLockpick") total += it.m_stack;
            }
            return total;
        }

        private static Sprite _white;
        private static Sprite WhiteSprite()
        {
            if (_white == null)
            {
                var t = Texture2D.whiteTexture;
                _white = Sprite.Create(t, new Rect(0f, 0f, t.width, t.height), new Vector2(0.5f, 0.5f), 100f);
            }
            return _white;
        }

        private static Sprite LockpickItemIcon()
        {
            var data = ObjectDB.instance?.GetItemPrefab("FiresLockpick")?.GetComponent<ItemDrop>()?.m_itemData;
            var icons = data?.m_shared?.m_icons;
            return (icons != null && icons.Length > 0) ? icons[0] : null;
        }

        private static Image NewImage(Transform parent, string name, Color c)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>(); img.color = c; return img;
        }

        private static TextMeshProUGUI NewText(Transform parent, string name, string text, float size, Color c)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text; t.fontSize = size; t.color = c; t.alignment = TextAlignmentOptions.Center; t.raycastTarget = false; t.enableWordWrapping = false;
            return t;
        }

        // Force-clear on logout so a modal open at logout can't leak the input block (matches EntryDoorPanel's guard).
        [HarmonyPatch(typeof(ZNet), "Shutdown")]
        internal static class ZNet_Shutdown_Guard
        {
            private static void Postfix() { if (!IsClientOnly()) { try { Close(); } catch { } } }
        }
    }

    /// <summary>The per-frame minigame driver: drag pick → raise nearest pin → tap E to set. TUNING constants up top.</summary>
    internal sealed class LockpickController : MonoBehaviour
    {
        // ── TUNING (housing-local space; dialled against a render) ────────────────────────────────────────────────
        public const float HousingW = 820f, HousingH = 683f;         // display of the 1200x1000 housing sprite (scale 0.6833)
        private const float PinW = 41f;                               // 60x208 pin cell @ 0.6833 (sliced from the aligned pins art)
        private static readonly float PinH = PinW * (208f / 60f);     // keep pin-cell aspect
        private const float PickW = 700f;                             // pick STRETCHED longer (user); height kept ~same
        private const float PickH = 52f;                              // fixed height (aspect not preserved on the pick)
        // Housing (hollow-cylinder) and pins are the SAME 1200x1000 canvas and overlap perfectly; cylinders/pins sit at
        // source x 475/548/620/692/765, y 420. SlotX = (cx-600)*0.6833, SlotRestY = (500-420)*0.6833 — the pins' true home.
        private static readonly float[] SlotX = { -85f, -36f, 14f, 63f, 113f };
        private const float SlotRestY = 55f;                          // pin rest = its exact home in the art (perfect overlap)
        private const float RaiseRange = 58f;                         // FULL throw — bolts reach the arch, springs compress behind the frame
        private const float SweetMin = 0.40f;                         // a pin must rise ≥ this fraction of the throw before its sweet spot (user's lower line)
        private const float SweetMax = 0.90f;                         // ...and no higher than this — the top bar (user's upper line)
        private const float PickBottomY = -25f, PickTopY = 60f;       // pick TIP Y band — the tip follows the crosshair within it
        private const float RaiseDragDist = 58f;                      // crosshair-Y drag (housing-local) for a FULL pin raise (1:1 with the throw)
        private const float PickPivotX = 0.977f;                      // pivot X at the hook tip so the pick TIP is the crosshair anchor (calibrated via widget)
        private const float PickPivotY = 0.760f;                      // pivot Y at the hook tip (the hook curls UP, so above centre) — calibrated via widget

        private RectTransform _assembly;
        private RectTransform[] _pinRT;
        private RectTransform _pickRT;
        private Image[] _pinImg;
        private Image _pickImg;
        private float[] _progress, _sweetStart, _sweetEnd, _shakeAmp;
        private float[] _flashT;                                      // per-pin flash countdown (set = gold, miss = red)
        private Color[] _flashC;
        private bool[] _isSet, _shaking;
        private int _n, _active = -1;
        private int _setCount;                                        // pins seated so far — drives the set-click pitch ramp
        private int _dragPin = -1;                                    // pin latched on LMB-down (the one being raised)
        private float _dragStartY;                                    // crosshair-Y at LMB-down — raise is relative to this
        private float _pickTilt;                                      // current pick lever tilt (deg, around the tip pivot)
        private float _timeLeft, _timeLimit = 30f, _breakChance, _skillFactor;
        private int _lastTickSecond = -1;
        private Action _onSuccess, _onFail, _onBreak;
        private bool _done;
        private bool _broke;   // set when a fumble snaps the pick — starts the snap beat after the pin loop
        private enum EndBeat { None, Unlocked, Snapped }
        private EndBeat _beat;                                        // the short cinematic beat before the panel resolves
        private float _beatT;
        private Vector2 _assemblyBase, _pickSnapPos;
        public Image TimerFill;
        public Image Flash;

        // Skill effectiveness caps (applied over the quality baseline at skill factor 1.0 = Lockpicking 100).
        private const float SkillWindowBonus = 0.08f;   // sweet-spot window widened by up to this (base ~0.12 → ~0.20)
        private const float SkillTimeBonus = 0.60f;      // timer extended by up to this fraction (+60% at mastery)

        // ── End-beat + feedback tuning ────────────────────────────────────────────────────────────────────────
        private const float UnlockBeatDur = 1.1f;       // gold flash + settle pulse before the panel closes on success
        private const float SnapBeatDur = 1.25f;        // red flash + shake while the broken pick falls, then close
        private const float PickMaxTiltDeg = 6.5f;      // handle dips as you lever a pin up (rotation around the tip pivot)
        private const float SnapFallDeg = 32f;          // the snapped pick's handle keels over this far...
        private const float SnapFallDist = 46f;         // ...while dropping this many units
        private const float ShakeAmp = 9f;              // assembly screen-shake at the moment of the snap
        private const float UrgencyFrac = 0.25f;        // timer fraction under which the bar pulses red + ticks
        private const float PinRetainBase = 0.25f;      // chance a seated pin survives the snap's jolt at Lockpicking 0...
        private const float PinRetainSkillBonus = 0.75f;// ...rising to GUARANTEED at Lockpicking 100 — mastery never loses a set pin
        private const float PinFlashDur = 0.35f;
        private static readonly Color PinSetTint = new Color(1f, 0.86f, 0.55f, 1f);    // a seated pin stays warm gold
        private static readonly Color PinFlashGold = new Color(1f, 0.97f, 0.75f, 1f);
        private static readonly Color MissFlashRed = new Color(1f, 0.38f, 0.3f, 1f);
        private static readonly Color UnlockFlash = new Color(1f, 0.85f, 0.45f, 1f);
        private static readonly Color SnapFlash = new Color(1f, 0.16f, 0.1f, 1f);

        public void Init(RectTransform assembly, Sprite pinSprite, Sprite pickSprite, string quality, int pinCount, float breakChance, float skillFactor, Action onSuccess, Action onFail, Action onBreak)
        {
            _assembly = assembly; _n = pinCount; _breakChance = breakChance; _skillFactor = skillFactor; _onSuccess = onSuccess; _onFail = onFail; _onBreak = onBreak;
            _timeLimit = quality == "Very Hard" ? 22f : quality == "Hard" ? 26f : quality == "Easy" ? 40f : 30f;
            _timeLimit *= 1f + SkillTimeBonus * skillFactor;   // Lockpicking buys more time on the lock
            _timeLeft = _timeLimit;
            float window = quality == "Very Hard" ? 0.08f : quality == "Hard" ? 0.10f : quality == "Easy" ? 0.16f : 0.12f;
            window += SkillWindowBonus * skillFactor;           // Lockpicking widens every pin's sweet spot

            _pinRT = new RectTransform[_n]; _pinImg = new Image[_n]; _progress = new float[_n]; _sweetStart = new float[_n]; _sweetEnd = new float[_n];
            _isSet = new bool[_n]; _shaking = new bool[_n]; _shakeAmp = new float[_n];
            _flashT = new float[_n]; _flashC = new Color[_n];
            _assemblyBase = _assembly.anchoredPosition;

            // DISTINCT sweet spot per pin: split the usable band [SweetMin, SweetMax-window] into N equal sub-bands and
            // hand each pin its OWN sub-band (Fisher-Yates shuffled so the heights aren't a predictable low→high ramp).
            // Every pin therefore rattles/locks at a clearly different height — no two share a sweet spot.
            float sweetSpan = Mathf.Max(0.0001f, (SweetMax - window) - SweetMin);
            float sweetStep = sweetSpan / Mathf.Max(1, _n);
            int[] sweetBand = new int[_n];
            for (int b = 0; b < _n; b++) sweetBand[b] = b;
            for (int b = _n - 1; b > 0; b--) { int j = UnityEngine.Random.Range(0, b + 1); int tmp = sweetBand[b]; sweetBand[b] = sweetBand[j]; sweetBand[j] = tmp; }

            for (int i = 0; i < _n; i++)
            {
                // Pins are siblings of the housing, drawn BEFORE it (SetAsFirstSibling) so the solid frame occludes the
                // rising spring and the open cylinder reveals the bolt. Placed at the pin's exact home in the art.
                var img = new GameObject("Pin" + i, typeof(RectTransform)).AddComponent<Image>();
                img.transform.SetParent(_assembly, false); img.sprite = pinSprite; img.preserveAspect = true; img.raycastTarget = false;
                img.rectTransform.SetAsFirstSibling();
                var rt = img.rectTransform; rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(PinW, PinH);
                rt.anchoredPosition = new Vector2(SlotX[i], SlotRestY);
                _pinRT[i] = rt;
                _pinImg[i] = img;
                // This pin's sweet spot = its shuffled sub-band + a little jitter, clamped inside the user's min/max lines.
                float jitter = UnityEngine.Random.Range(0f, sweetStep * 0.6f);
                _sweetStart[i] = Mathf.Clamp(SweetMin + sweetBand[i] * sweetStep + jitter, SweetMin, SweetMax - window);
                _sweetEnd[i] = _sweetStart[i] + window;
            }

            // Pick pivoted near its HOOK so the tip is the anchor; the rod extends LEFT and starts at the DEEPEST
            // (rightmost) pin — you slide it left to the shallowest. Rendered IN FRONT of the housing (last sibling).
            var pk = new GameObject("Pick", typeof(RectTransform)).AddComponent<Image>();
            pk.transform.SetParent(_assembly, false); pk.sprite = pickSprite; pk.preserveAspect = false; pk.raycastTarget = false;
            _pickImg = pk;
            _pickRT = pk.rectTransform; _pickRT.anchorMin = _pickRT.anchorMax = new Vector2(0.5f, 0.5f); _pickRT.pivot = new Vector2(PickPivotX, PickPivotY);
            _pickRT.sizeDelta = new Vector2(PickW, PickH);
            _pickRT.anchoredPosition = new Vector2(SlotX[_n - 1], PickBottomY);
            _pickRT.SetAsLastSibling();
        }

        private void Update()
        {
            if (_done) return;
            if (_beat != EndBeat.None) { UpdateEndBeat(); return; }

            _timeLeft -= Time.deltaTime;
            float ratio = Mathf.Clamp01(_timeLeft / _timeLimit);
            if (TimerFill != null)
            {
                TimerFill.fillAmount = ratio;
                Color c = ratio > 0.5f ? Color.Lerp(Color.yellow, Color.green, (ratio - 0.5f) * 2f) : Color.Lerp(Color.red, Color.yellow, ratio * 2f);
                // In the red the bar PULSES and a clock tick marks every remaining second.
                if (ratio <= UrgencyFrac)
                {
                    c = Color.Lerp(c, new Color(1f, 0.08f, 0.05f, 1f), 0.5f + 0.5f * Mathf.Sin(Time.time * 10f));
                    int sec = Mathf.CeilToInt(_timeLeft);
                    if (sec != _lastTickSecond) { _lastTickSecond = sec; LockpickAudio.PlayTick(1f - ratio / UrgencyFrac); }
                }
                TimerFill.color = c;
            }
            if (_timeLeft <= 0f) { End(false); return; }
            if (Input.GetKeyDown(KeyCode.Escape)) { End(false); return; }

            // Crosshair (free cursor) in housing-local space — this is what the pick tip locks onto for left-right.
            Vector2 cursor;
            if (!CursorLocal(out cursor)) cursor = _pickRT.anchoredPosition;

            bool held = Input.GetMouseButton(0);

            // LMB-down over a pin latches it; the raise is a RELATIVE drag from the press height (no jump on click).
            if (Input.GetMouseButtonDown(0))
            {
                int p = NearestPin(cursor.x);
                if (p >= 0 && !_isSet[p]) { _dragPin = p; _dragStartY = cursor.y; }
            }
            if (!held) _dragPin = -1;

            // Pick tip: X always tracks the crosshair (or snaps onto the latched pin while raising); Y follows the
            // crosshair within the band. Left-right is therefore ALWAYS available; up is the click-drag.
            float tipX = _dragPin >= 0 ? SlotX[_dragPin] : Mathf.Clamp(cursor.x, SlotX[0] - 40f, SlotX[_n - 1] + 40f);
            float tipY = Mathf.Clamp(cursor.y, PickBottomY, PickTopY);
            _pickRT.anchoredPosition = new Vector2(tipX, tipY);

            _active = _dragPin >= 0 ? _dragPin : NearestPin(tipX);

            for (int i = 0; i < _n; i++)
            {
                if (_isSet[i]) continue;
                if (i == _dragPin && held)
                {
                    _progress[i] = Mathf.Clamp01((cursor.y - _dragStartY) / RaiseDragDist);
                    _shaking[i] = _progress[i] >= _sweetStart[i] && _progress[i] <= _sweetEnd[i];
                    _shakeAmp[i] = Proximity(i);   // 0 far → 1 in the sweet spot; drives jiggle + scrape volume
                    if (Input.GetKeyDown(KeyCode.E))
                    {
                        if (_shaking[i]) SetPin(i);
                        else { MissPin(i); _dragStartY = cursor.y; }   // re-baseline the drag after a fumble
                    }
                }
                else
                {
                    _shaking[i] = false;
                    _shakeAmp[i] = 0f;
                    _progress[i] = Mathf.MoveTowards(_progress[i], 0f, 3f * Time.deltaTime);
                }
            }

            if (_broke) { StartSnapBeat(); return; }

            // Drag scrape: louder + higher-pitched as the raised pin nears its sweet spot; silent when not raising.
            if (_dragPin >= 0 && held) LockpickAudio.SetDragIntensity(_shakeAmp[_dragPin]);
            else LockpickAudio.StopDrag();

            // Lever tilt: the handle dips as the tip pushes a pin up (rotation around the tip pivot), easing back flat
            // when the drag releases. Pure feel — the tip anchor is the pivot, so nothing moves out of calibration.
            float targetTilt = (_dragPin >= 0 && held) ? _progress[_dragPin] * PickMaxTiltDeg : 0f;
            _pickTilt = Mathf.Lerp(_pickTilt, targetTilt, 14f * Time.deltaTime);
            _pickRT.localRotation = Quaternion.Euler(0f, 0f, _pickTilt);

            UpdateVisuals();

            bool all = true; for (int i = 0; i < _n; i++) if (!_isSet[i]) all = false;
            if (all) { StartUnlockBeat(); return; }
        }

        private bool CursorLocal(out Vector2 local)
        {
            local = Vector2.zero;
            var canvas = _assembly.GetComponentInParent<Canvas>();
            var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            return RectTransformUtility.ScreenPointToLocalPointInRectangle(_assembly, Input.mousePosition, cam, out local);
        }

        private int NearestPin(float x)
        {
            int best = -1; float bd = float.MaxValue;
            for (int i = 0; i < _n; i++)
            {
                if (_isSet[i]) continue;
                float d = Mathf.Abs(SlotX[i] - x);
                if (d < bd && d < 40f) { bd = d; best = i; }
            }
            return best;
        }

        // How close pin i is to its sweet spot: 1 inside the window, ramping to 0 over WarmRange beyond it. Drives the
        // "hotter as you approach" cue — bigger/faster jiggle and louder scrape the nearer the pin is.
        private const float WarmRange = 0.35f;
        private float Proximity(int i)
        {
            float center = (_sweetStart[i] + _sweetEnd[i]) * 0.5f;
            float half = (_sweetEnd[i] - _sweetStart[i]) * 0.5f;
            float dist = Mathf.Abs(_progress[i] - center);
            return dist <= half ? 1f : Mathf.Clamp01(1f - (dist - half) / WarmRange);
        }

        private void SetPin(int i)
        {
            // Locked: freeze it dead still at the shear line. Zero the shake amp — the main loop `continue`s past set
            // pins so it never clears otherwise, and UpdateVisuals would keep jiggling a locked pin forever.
            _isSet[i] = true; _shaking[i] = false; _shakeAmp[i] = 0f;
            _flashT[i] = PinFlashDur; _flashC[i] = PinFlashGold;
            LockpickAudio.PlayPinSet(_setCount);   // each seated pin clicks a semitone higher
            _setCount++;
            FiresDungeonMaster.Editor.LockpickSkill.GrantXp(FiresDungeonMaster.Editor.LockpickSkill.XpPerPin);   // Lockpicking XP per pin set
        }

        private void MissPin(int i)
        {
            _progress[i] = 0f; _shaking[i] = false;
            _flashT[i] = PinFlashDur; _flashC[i] = MissFlashRed;
            LockpickAudio.PlayMiss();
            if (_breakChance > 0f && UnityEngine.Random.value < _breakChance)
                _broke = true;   // the pick snapped — the snap beat starts after the pin loop
        }

        private void UpdateVisuals()
        {
            for (int i = 0; i < _n; i++)
            {
                float y = SlotRestY + _progress[i] * RaiseRange;   // home + rise; spring compresses behind the frame
                float x = SlotX[i];
                float amp = _shakeAmp[i];
                // Never jiggle a LOCKED pin — it sits dead still at its shear line.
                if (!_isSet[i] && amp > 0.01f) x += Mathf.Sin(Time.time * (55f + 55f * amp)) * (4f * amp);   // jiggle grows as you near the sweet spot
                _pinRT[i].anchoredPosition = new Vector2(x, y);

                // Pin colour: seated pins hold a warm gold tint; a set (gold) or miss (red) flash decays onto that base.
                Color baseC = _isSet[i] ? PinSetTint : Color.white;
                if (_flashT[i] > 0f)
                {
                    _flashT[i] -= Time.deltaTime;
                    _pinImg[i].color = Color.Lerp(baseC, _flashC[i], Mathf.Clamp01(_flashT[i] / PinFlashDur));
                }
                else _pinImg[i].color = baseC;
            }
        }

        // The last pin seated: hold the moment — unlock click, gold flash, a settle pulse — THEN resolve. Closing the
        // panel the same frame as the 5th pin read as a glitch; the beat sells the unlock. Input is dead during it.
        private void StartUnlockBeat()
        {
            _beat = EndBeat.Unlocked; _beatT = 0f;
            LockpickAudio.StopDrag();
            LockpickAudio.PlayDoorUnlock();
        }

        // The pick snapped: break SFX (randomly pitched), red flash, a short screen shake while the pick keels over
        // and falls away — THEN the break resolves (consume + message via onBreak; the game continues on a spare).
        private void StartSnapBeat()
        {
            _beat = EndBeat.Snapped; _beatT = 0f;
            _pickSnapPos = _pickRT.anchoredPosition;
            LockpickAudio.StopDrag();
            LockpickAudio.PlayBreak();

            // The jolt can pop seated pins back out — each independently rolls to stay, and Lockpicking raises the
            // odds. Popped pins lose their gold, flash red and visibly drop with the rest during the beat.
            float retain = PinRetainBase + PinRetainSkillBonus * _skillFactor;
            int kept = 0;
            for (int i = 0; i < _n; i++)
            {
                if (!_isSet[i]) continue;
                if (UnityEngine.Random.value <= retain) { kept++; continue; }
                _isSet[i] = false;
                _flashT[i] = PinFlashDur; _flashC[i] = MissFlashRed;
            }
            _setCount = kept;   // the set-click pitch ramp restarts from the surviving count
        }

        private void UpdateEndBeat()
        {
            _beatT += Time.deltaTime;
            if (_beat == EndBeat.Unlocked)
            {
                float t = Mathf.Clamp01(_beatT / UnlockBeatDur);
                if (Flash != null) Flash.color = new Color(UnlockFlash.r, UnlockFlash.g, UnlockFlash.b, 0.4f * (1f - t));
                _assembly.localScale = Vector3.one * (1f + 0.03f * Mathf.Sin(Mathf.Clamp01(_beatT / 0.3f) * Mathf.PI));
                UpdateVisuals();   // lets the final pin's gold flash play out
                if (_beatT >= UnlockBeatDur) End(true);
                return;
            }

            float ft = Mathf.Clamp01(_beatT / SnapBeatDur);
            float fall = ft * ft;   // the broken pick accelerates as it drops
            _pickRT.localRotation = Quaternion.Euler(0f, 0f, Mathf.Lerp(_pickTilt, SnapFallDeg, fall));
            _pickRT.anchoredPosition = _pickSnapPos + Vector2.down * (SnapFallDist * fall);
            var pc = _pickImg.color; pc.a = 1f - 0.75f * ft; _pickImg.color = pc;
            float shake = ShakeAmp * (1f - Mathf.Clamp01(_beatT / 0.45f));
            _assembly.anchoredPosition = _assemblyBase + new Vector2(Mathf.Sin(_beatT * 71f), Mathf.Cos(_beatT * 47f)) * shake;
            if (Flash != null) Flash.color = new Color(SnapFlash.r, SnapFlash.g, SnapFlash.b, 0.45f * (1f - ft));
            for (int i = 0; i < _n; i++)
                if (!_isSet[i]) { _progress[i] = Mathf.MoveTowards(_progress[i], 0f, 3f * Time.deltaTime); _shakeAmp[i] = 0f; _shaking[i] = false; }
            UpdateVisuals();   // raised pins visibly drop as tension is lost
            if (_beatT >= SnapBeatDur) ResolveSnap();
        }

        private void End(bool won)
        {
            if (_done) return;
            _done = true;
            LockpickAudio.StopDrag();
            LockpickPanel.Finish(won, _onSuccess, _onFail);
        }

        // Snap beat finished: fire onBreak (the caller consumes a lockpick + shows the snap cue), then either hand the
        // player a FRESH pick — spares remain, so the minigame continues with the SURVIVING pins kept — or close now
        // that there is nothing left to pick with. A break never fires onFail; it is not the same as running out of time.
        private void ResolveSnap()
        {
            _onBreak?.Invoke();
            LockpickPanel.RefreshPickCount();
            if (LockpickPanel.CountLockpicks() > 0) { ContinueWithFreshPick(); return; }
            if (_done) return;
            _done = true;
            LockpickAudio.StopDrag();
            LockpickPanel.Close();
        }

        // A fresh pick out of the pouch: clear the snap state and restore the pick visual where the old one broke.
        // Surviving pins stay seated; the timer resumes where the beat paused it; the player must re-grab (LMB) a pin.
        private void ContinueWithFreshPick()
        {
            _broke = false;
            _beat = EndBeat.None; _beatT = 0f;
            _dragPin = -1;
            _pickTilt = 0f;
            _pickRT.localRotation = Quaternion.identity;
            _pickRT.anchoredPosition = _pickSnapPos;
            var pc = _pickImg.color; pc.a = 1f; _pickImg.color = pc;
            _assembly.anchoredPosition = _assemblyBase;
            if (Flash != null) Flash.color = new Color(1f, 1f, 1f, 0f);
        }
    }
}
